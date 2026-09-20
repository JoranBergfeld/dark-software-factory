using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public enum ModelCapabilityState { Verified, Partial, Blocked, Failed, Canceled, Unknown }
public enum ModelCapabilityReason
{
    None, MissingDeployment, IdentityMismatch, FamilyDiversity, MalformedResponse, PermissionDenied,
    NetworkUnavailable, UnsupportedTarget, AuthorityChanged, Expired, RateLimited,
}

public sealed record ModelRouteSelection(
    string Name, bool Create, string AccountResourceId, string Deployment, string Format, string Family,
    string Model, string Version, string Sku, int Capacity, int Dimensions, string TenantId, string Location,
    string Endpoint, bool ModelServingOnly, string ConnectivityReview, string InferenceRoleDefinitionId);

public sealed record ModelTopologySelection(
    string RuntimeImage, string AudienceClientId, int ValidForSeconds, IReadOnlyList<ModelRouteSelection> Routes);

public sealed record ModelResourceObservation(string ResourceId, string Revision, string Incarnation);
public sealed record ReviewedModelRoute(ModelRouteSelection Selection,
    ModelResourceObservation AccountObservation, ModelResourceObservation? DeploymentObservation);

public sealed record FactoryModelsConfiguration(
    int SchemaVersion, Guid FactoryId, string PlanFingerprint, string ProductEndpoint, string ProfilePin,
    string RuntimeImage, string AudienceClientId, int ValidForSeconds, IReadOnlyList<string> RouteHashes,
    IReadOnlyList<string> BindingHashes)
{
    public string HostTemplateSha256 { get; init; } = "";
    public IReadOnlyList<string> GrantHashes { get; init; } = [];
}

public sealed record ModelAccessGrant(string Target, string PrincipalId, string Scope, string RoleDefinitionId, string RoleRevision);

public sealed record FactoryModels(
    Guid OperationId, string ApprovalFingerprint, FactoryModelsConfiguration Configuration,
    ModelCapabilityState State, string Stage)
{
    public string Detail { get; init; } = "Model capability preview only; not council readiness or activation.";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ReviewedModelRoute>? Routes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FoundationOperation>? Bindings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModelAccessGrant>? Grants { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FoundationOperation>? Effects { get; init; }
    public int OperationCount { get; init; }
    public IReadOnlyList<string> EffectHashes { get; init; } = [];
    public string? HostEndpoint { get; init; }
    public Guid? CheckId { get; init; }
    public string? LastProofReference { get; init; }
    public string? LastProofSha256 { get; init; }
    public DateTimeOffset? VerifiedUntil { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelCapabilityProof? Proof { get; init; }
}

public sealed record ModelWorkloadObservation(
    string Route, string ConfigurationRevision, ModelCapabilityState State, ModelCapabilityReason Reason,
    string? ResponseModel, string? RequestId, string? OutputSha256, int OutputCharacters, int Dimensions,
    IReadOnlyList<IReadOnlyList<string>> Groups);

public sealed record ModelCapabilityProof(
    Guid FactoryId, Guid OperationId, Guid CheckId, string ConfigurationRevision, string PrincipalId,
    DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt, ModelCapabilityState State,
    IReadOnlyList<ModelWorkloadObservation> Observations)
{
    public const string CoverageStatement = "Nonsensitive capability probes, not application evidence, proposals, council readiness or activation.";
    public string Coverage { get; init; } = CoverageStatement;
}

public sealed record ModelText(string Id, string Text);
public sealed record ModelTextResult(string Text, ModelWorkloadObservation Observation);
public sealed record ModelDeduplicationResult(IReadOnlyList<IReadOnlyList<string>> Groups, ModelWorkloadObservation Observation);

/// <summary>All routes share one reviewed topology; callers must retain the returned configuration revision.</summary>
public interface IFactoryModelWorkloads
{
    Task<ModelTextResult> SynthesizeAsync(string input, CancellationToken token);
    Task<ModelTextResult> JudgeAsync(string juror, string input, CancellationToken token);
    Task<ModelDeduplicationResult> DeduplicateAsync(IReadOnlyList<ModelText> input, CancellationToken token);
}

public sealed class ModelCapabilityException(ModelCapabilityReason reason, string route, string detail) : Exception(detail)
{
    public ModelCapabilityReason Reason { get; } = reason;
    public string Route { get; } = route;
}

public static partial class FactoryModelsValidation
{
    private static readonly JsonSerializerOptions HashJson = new(FactoryGitHubJson.Options) { IgnoreReadOnlyProperties = false };
    public const string ProviderApi = "2024-10-01";
    public static IReadOnlyList<string> RouteNames { get; } = ["synthesis", "embedding", "juror-1", "juror-2", "juror-3"];
    public static string Hash<T>(T value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, HashJson)));
    public static string DeploymentTarget(ModelRouteSelection route) => route.AccountResourceId + "/deployments/" + route.Deployment;

    public static void ValidateSelection(ModelTopologySelection selection, FactoryReservationIdentity identity)
    {
        if (!Regex.IsMatch(selection.RuntimeImage, "^[a-z0-9.-]+/[a-zA-Z0-9_./-]+@sha256:[0-9a-f]{64}$")
            || selection.RuntimeImage.Length > 512 || !Guid.TryParse(selection.AudienceClientId, out var audience)
            || audience == Guid.Empty || selection.ValidForSeconds is < 60 or > 86400)
            throw Failure(ModelCapabilityReason.UnsupportedTarget, "host", "Digest-pinned runtime image, existing API audience and 60..86400 second proof lifetime required.");
        if (selection.Routes.Count != 5 || selection.Routes.Any(r => r is null) || !selection.Routes.Select(r => r.Name).SequenceEqual(RouteNames))
            throw Failure(ModelCapabilityReason.FamilyDiversity, "topology", "Exactly synthesis, embedding, juror-1, juror-2, juror-3 required in that order.");
        foreach (var route in selection.Routes)
        {
            var accountPattern = "^/subscriptions/" + identity.SubscriptionId
                + "/resourcegroups/[-a-z0-9_.]+/providers/microsoft.cognitiveservices/accounts/[-a-z0-9_.]+$";
            if (!Regex.IsMatch(route.AccountResourceId, accountPattern) || route.AccountResourceId.Length > 350
                || !Regex.IsMatch(route.Deployment, "^[-a-zA-Z0-9_.]{1,64}$")
                || route.TenantId != identity.TenantId || !Regex.IsMatch(route.Location, "^[a-z0-9]{1,40}$")
                || !route.ModelServingOnly || string.IsNullOrWhiteSpace(route.ConnectivityReview)
                || route.ConnectivityReview.Length > 256 || route.ConnectivityReview.Any(char.IsControl)
                || route.Capacity is < 1 or > 1000 || route.Sku is not ("Standard" or "GlobalStandard" or "DataZoneStandard")
                || !Regex.IsMatch(route.Model, "^[-a-zA-Z0-9_.]{1,100}$") || !Regex.IsMatch(route.Version, "^[-a-zA-Z0-9_.]{1,40}$")
                || !Regex.IsMatch(route.InferenceRoleDefinitionId, "^/subscriptions/" + identity.SubscriptionId
                    + "/providers/microsoft.authorization/roledefinitions/[0-9a-f-]{36}$"))
                throw Failure(ModelCapabilityReason.UnsupportedTarget, route.Name, "Explicit same-tenant/subscription model-serving account, location, private connectivity review and bounded native deployment/role selection required.");
            var endpoint = FactoryGovernance.NormalizeEndpoint(route.Endpoint);
            var account = route.AccountResourceId.Split('/').Last();
            if (endpoint != route.Endpoint || !new[] { $"https://{account}.openai.azure.com", $"https://{account}.services.ai.azure.com" }.Contains(endpoint))
                throw Failure(ModelCapabilityReason.UnsupportedTarget, route.Name, "Supported Azure custom-subdomain native v1 root required; arbitrary gateways and endpoint aliases are not identity.");
            if (route.Create != (route.Name is "synthesis" or "embedding")
                || route.Create && route.Deployment != "df-" + identity.FactoryId.ToString("N")[..14] + "-" + route.Name)
                throw Failure(ModelCapabilityReason.UnsupportedTarget, route.Name, "Only reserved factory synthesis/embedding deployment creation is permitted; all three jurors must already exist.");
            var supported = route.Name == "embedding"
                ? route.Format == "OpenAI" && route.Family == "text-embedding" && route.Model is "text-embedding-3-small" or "text-embedding-3-large"
                    && route.Dimensions == (route.Model == "text-embedding-3-small" ? 1536 : 3072)
                : route.Dimensions == 0 && (route.Format, route.Family) is ("OpenAI", "gpt") or ("DeepSeek", "deepseek") or ("xAI", "grok")
                    && route.Model.StartsWith(route.Family + "-", StringComparison.OrdinalIgnoreCase);
            if (!supported || route.Name == "synthesis" && route.Format != "OpenAI")
                throw Failure(ModelCapabilityReason.IdentityMismatch, route.Name, "Unsupported provider/model/family or embedding dimensions; no model substitution.");
        }
        if (selection.Routes.Select(DeploymentTarget).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5
            || selection.Routes.Skip(2).Select(r => r.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw Failure(ModelCapabilityReason.FamilyDiversity, "jurors", "Three distinct native deployments from three distinct model families required, not three aliases.");
        foreach (var account in selection.Routes.GroupBy(r => r.AccountResourceId))
            if (account.Select(r => (r.Endpoint, r.Location, r.InferenceRoleDefinitionId, r.ConnectivityReview)).Distinct().Count() != 1)
                throw Failure(ModelCapabilityReason.IdentityMismatch, "topology", "Shared account reviews must agree exactly.");
    }

    public static ModelResourceObservation ObserveAccount(ModelRouteSelection route, JsonElement resource)
    {
        var properties = resource.GetProperty("properties");
        if (!Same(resource.GetProperty("id").GetString(), route.AccountResourceId)
            || resource.GetProperty("location").GetString() != route.Location
            || resource.GetProperty("kind").GetString() is not ("AIServices" or "OpenAI")
            || properties.GetProperty("provisioningState").GetString() != "Succeeded"
            || !DeclaredEndpoint(properties, route.Endpoint)
            || properties.GetProperty("customSubDomainName").GetString() != route.AccountResourceId.Split('/').Last()
            || properties.GetProperty("publicNetworkAccess").GetString() != "Disabled")
            throw Failure(ModelCapabilityReason.UnsupportedTarget, route.Name, "Current native model account/endpoint/location/private-access metadata differs; network administrator must reconcile without public-access relaxation.");
        var incarnation = resource.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset().ToString("O");
        return new(route.AccountResourceId, Hash(new
        {
            route.AccountResourceId, route.Endpoint, route.Location, incarnation,
            kind = resource.GetProperty("kind").GetString(),
            network = properties.GetProperty("publicNetworkAccess"),
            acls = properties.TryGetProperty("networkAcls", out var acls) ? acls : default(JsonElement?),
            connections = properties.TryGetProperty("privateEndpointConnections", out var connections) ? connections : default(JsonElement?),
        }), incarnation);
    }

    private static bool DeclaredEndpoint(JsonElement properties, string endpoint)
    {
        var candidates = new List<string?> { properties.GetProperty("endpoint").GetString() };
        if (properties.TryGetProperty("endpoints", out var endpoints))
            candidates.AddRange(endpoints.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()));
        return candidates.Any(value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.AbsolutePath is "/" or "/models" or "/models/" or "/openai/v1" or "/openai/v1/"
            && uri.GetLeftPart(UriPartial.Authority) == endpoint);
    }

    public static ModelResourceObservation ObserveDeployment(ModelRouteSelection route, JsonElement resource)
    {
        var target = DeploymentTarget(route);
        var properties = resource.GetProperty("properties");
        var model = properties.GetProperty("model");
        if (!Same(resource.GetProperty("id").GetString(), target) || properties.GetProperty("provisioningState").GetString() != "Succeeded"
            || model.GetProperty("format").GetString() != route.Format || model.GetProperty("name").GetString() != route.Model
            || model.GetProperty("version").GetString() != route.Version
            || model.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String && source.GetString()!.Length != 0
            || model.TryGetProperty("sourceAccount", out var account) && account.ValueKind == JsonValueKind.String && account.GetString()!.Length != 0
            || resource.GetProperty("sku").GetProperty("name").GetString() != route.Sku
            || resource.GetProperty("sku").GetProperty("capacity").GetInt32() != route.Capacity
            || route.Create && properties.GetProperty("versionUpgradeOption").GetString() != "NoAutoUpgrade")
            throw Failure(ModelCapabilityReason.IdentityMismatch, route.Name, "Actual native deployment provider/model/version/capacity differs from review; aliases or replacement models are not proof.");
        var incarnation = resource.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset().ToString("O");
        var etag = resource.GetProperty("etag").GetString();
        if (string.IsNullOrWhiteSpace(etag) || etag.Length > 256) throw new JsonException();
        return new(target, Hash(new { target, incarnation, etag, route.Format, route.Model, route.Version, route.Sku, route.Capacity }), incarnation);
    }

    public static ModelCapabilityException Failure(ModelCapabilityReason reason, string route, string detail) => new(reason, route, detail);
    private static bool Same(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
