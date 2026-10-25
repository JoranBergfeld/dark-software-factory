using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public static partial class FactoryModelsValidation
{
    public static IReadOnlyList<string> BindingNames { get; } = ["model", "code", "hosting"];
    public const string OperationKind = "decide-model-operation";
    public const string ConfigReaderRole = "516239f1-63e1-4d78-a4de-a74fb236a071";
    public static FactoryModels Persisted(FactoryModels value) => value with { Routes = null, Bindings = null, Grants = null, Effects = null, Proof = null };
    public static string HostTarget(FactoryModels value) =>
        value.Bindings![2].Target[..value.Bindings[2].Target.IndexOf("/providers/", StringComparison.Ordinal)]
        + "/providers/microsoft.app/containerapps/df-" + value.Configuration.FactoryId.ToString("N")[..14] + "-models";
    public static ModelAccessGrant Grant(string scope, string role, string principal, string revision)
    {
        var name = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(principal + ":" + scope + ":" + role))[..16]);
        return new(scope + "/providers/microsoft.authorization/roleassignments/" + name, principal, scope, role, revision);
    }

    public static string ObserveRole(string roleId, JsonElement resource)
    {
        if (!Same(resource.GetProperty("id").GetString(), roleId)) throw new JsonException();
        var properties = resource.GetProperty("properties");
        var permissions = properties.GetProperty("permissions").EnumerateArray().ToArray();
        var config = roleId.EndsWith("/" + ConfigReaderRole, StringComparison.Ordinal);
        if (properties.GetProperty("type").GetString() != (config ? "BuiltInRole" : "CustomRole") || permissions.Length != 1)
            throw Failure(ModelCapabilityReason.UnsupportedTarget, "inference-role", "Existing reviewed narrow role required; no role definition creation or expansion.");
        var permission = permissions[0];
        var actions = Strings(permission, "actions");
        var data = Strings(permission, "dataActions");
        if (Strings(permission, "notActions").Length != 0 || Strings(permission, "notDataActions").Length != 0
            || config && (actions.Length != 0 || data.Length == 0 || data.Any(a =>
                !a.StartsWith("Microsoft.AppConfiguration/configurationStores/", StringComparison.OrdinalIgnoreCase) || !a.EndsWith("/read", StringComparison.OrdinalIgnoreCase)))
            || !config && (!actions.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(new[]
                { "Microsoft.CognitiveServices/accounts/read", "Microsoft.CognitiveServices/accounts/deployments/read",
                    "Microsoft.Authorization/roleAssignments/read", "Microsoft.Authorization/roleDefinitions/read" }.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                || !data.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(new[]
                { "Microsoft.CognitiveServices/accounts/OpenAI/deployments/chat/completions/action",
                    "Microsoft.CognitiveServices/accounts/OpenAI/deployments/embeddings/action",
                    "Microsoft.CognitiveServices/accounts/MaaS/chat/completions/action" }.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)))
            throw Failure(ModelCapabilityReason.UnsupportedTarget, "inference-role",
                "Role must contain only native OpenAI chat/embedding and MaaS chat inference plus account/deployment/assignment/role-definition metadata reads; no keys, stored completions, evidence, project data or writes.");
        return Hash(properties);
    }

    public static FactoryModels ReadPreparation(FactoryManifest manifest, IReadOnlyDictionary<string, string> values)
    {
        T Read<T>(string key) => JsonSerializer.Deserialize<T>(values[key], FactoryGitHubJson.Options) ?? throw new JsonException();
        var authority = Read<FactoryModels>(ProductConfigurationKeys.FactoryModels);
        var config = authority.Configuration;
        if (authority.OperationId == Guid.Empty || manifest.ModelsOperationId != authority.OperationId
            || manifest.ModelsApprovalFingerprint != authority.ApprovalFingerprint || Hash(config) != authority.ApprovalFingerprint
            || config.SchemaVersion != 1 || config.FactoryId != manifest.Identity.FactoryId
            || config.PlanFingerprint != manifest.Identity.PlanFingerprint || config.ProfilePin != manifest.ProfilePin
            || config.ProductEndpoint != "https://dsfo-" + config.FactoryId.ToString("N") + ".azconfig.io"
            || config.RouteHashes.Count != 5 || config.BindingHashes.Count != 3 || config.GrantHashes.Count is < 2 or > 6
            || authority.OperationCount is < 0 or > 10 || config.HostTemplateSha256.Length != 64
            || authority.Stage is "prepared" or "checking" or "observed"
                && (authority.OperationCount != config.GrantHashes.Count + 4 || authority.EffectHashes.Count != authority.OperationCount))
            throw new JsonException("Current reviewed model authority required.");
        var routes = Enumerable.Range(0, 5).Select(i => Read<ReviewedModelRoute>(ProductConfigurationKeys.ModelRoute(authority.OperationId, i))).ToArray();
        var bindings = BindingNames.Select(n => Read<FoundationOperation>(ProductConfigurationKeys.ModelBinding(authority.OperationId, n))).ToArray();
        var grants = Enumerable.Range(0, config.GrantHashes.Count).Select(i => Read<ModelAccessGrant>(ProductConfigurationKeys.ModelGrant(authority.OperationId, i))).ToArray();
        if (!routes.Select(Hash).SequenceEqual(config.RouteHashes) || !bindings.Select(Hash).SequenceEqual(config.BindingHashes)
            || !grants.Select(Hash).SequenceEqual(config.GrantHashes)) throw new JsonException();
        ValidateSelection(new(config.RuntimeImage, config.AudienceClientId, config.ValidForSeconds, routes.Select(r => r.Selection).ToArray()), manifest.Identity);
        for (var i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            if (binding.FactoryId != config.FactoryId || binding.State != FoundationOperationState.Completed
                || binding.Disposition != FoundationResourceDisposition.Created || binding.ResourceId != binding.Target
                || binding.Incarnation is null || binding.ProviderOperationId is null
                || binding.OperationId != bindings[0].OperationId || i < 2 && (binding.LogicalIdentity != (i == 0 ? "factory:council" : "factory:code-intent")
                    || !Guid.TryParse(binding.PrincipalId, out var principal) || principal == Guid.Empty
                    || !Guid.TryParse(binding.ClientId, out var client) || client == Guid.Empty
                    || binding.PrincipalId == manifest.Identity.AzureActorId))
                throw new JsonException();
        }
        if (bindings[0].PrincipalId == bindings[1].PrincipalId) throw new JsonException();
        var scope = bindings[0].Target[..bindings[0].Target.IndexOf("/providers/", StringComparison.Ordinal)]
            + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + config.FactoryId.ToString("N");
        var expected = new[] { (Scope: scope, Role: "/subscriptions/" + manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/" + ConfigReaderRole) }
            .Concat(routes.Select(r => (Scope: r.Selection.AccountResourceId, Role: r.Selection.InferenceRoleDefinitionId)).Distinct()).ToArray();
        if (expected.Length != grants.Length || grants.Where((g, i) => g != Grant(expected[i].Scope, expected[i].Role, bindings[0].PrincipalId!, g.RoleRevision)).Any())
            throw new JsonException();
        var effects = Enumerable.Range(0, authority.OperationCount).Select(i =>
            Read<FoundationOperation>(ProductConfigurationKeys.ModelOperation(authority.OperationId, i))).ToArray();
        authority = authority with { Routes = routes, Bindings = bindings, Grants = grants, Effects = effects };
        var targets = routes.Take(2).Select(r => DeploymentTarget(r.Selection)).Concat(grants.Select(g => g.Target))
            .Concat([HostTarget(authority), HostTarget(authority) + "/authconfigs/current"]).ToArray();
        for (var i = 0; i < effects.Length; i++)
        {
            var effect = effects[i];
            if (effect.Kind != OperationKind || effect.SchemaVersion != 1 || effect.OperationId != authority.OperationId
                || effect.FactoryId != config.FactoryId || effect.Index != i || effect.Target != targets[i] || effect.ClientRequestId == Guid.Empty)
                throw new JsonException();
            if (authority.Stage is "prepared" or "checking" or "observed")
            {
                if (effects.Length != targets.Length || authority.EffectHashes.Count != effects.Length || Hash(effect) != authority.EffectHashes[i]
                    || effect.State != FoundationOperationState.Completed || effect.Disposition != FoundationResourceDisposition.Created
                    || effect.ResourceId != effect.Target || effect.HttpStatus != 201 || effect.Incarnation is null || effect.NativeRevision is null
                    || !Guid.TryParse(effect.ProviderRequestId, out var request) || request == Guid.Empty
                    || !Guid.TryParse(effect.ProviderCorrelationId, out var correlation) || correlation == Guid.Empty)
                    throw new JsonException();
            }
        }
        return authority;
    }

    public static string[] Strings(JsonElement value, string name) =>
        value.GetProperty(name).EnumerateArray().Select(p => p.GetString() ?? throw new JsonException()).ToArray();
}
