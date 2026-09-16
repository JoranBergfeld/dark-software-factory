using System.Net;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryModelsService(IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    internal async Task<FactoryModels> PreviewAsync(FactoryReservationResult admission, ModelTopologySelection selection, CancellationToken token)
    {
        if (admission.State != FactoryReservationState.Reserved || admission.Foundation?.State != FactoryFoundationState.Inactive
            || admission.Plan is null || admission.Identity is null)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Current provenance-proven inactive foundation required for model preparation.");
        FactoryModelsValidation.ValidateSelection(selection, admission.Identity);
        var applicationScopes = admission.Plan.Selection.ApplicationResourceIds
            .Concat(admission.Plan.Selection.EvidenceBackends.Select(b => b.ResourceId)).ToArray();
        if (selection.Routes.Any(r => applicationScopes.Any(id => id.Equals(r.AccountResourceId, StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(r.AccountResourceId + "/", StringComparison.OrdinalIgnoreCase)
            || r.AccountResourceId.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase))))
            throw FactoryModelsValidation.Failure(ModelCapabilityReason.UnsupportedTarget, "model-serving",
                "Reviewed model-serving prerequisites must remain outside application/evidence membership; no application data authority or ownership takeover.");
        var bindings = new[] { "factory:council", "factory:code-intent", "hosting" }.Select(name =>
        {
            var binding = admission.Foundation.Operations.Single(o => name == "hosting"
                ? o.Target.EndsWith("-hosting", StringComparison.Ordinal) && o.Target.Contains("/managedenvironments/", StringComparison.Ordinal)
                : o.LogicalIdentity == name);
            if (binding.State != FoundationOperationState.Completed || binding.Disposition != FoundationResourceDisposition.Created
                || binding.ResourceId != binding.Target || binding.Incarnation is null || binding.ProviderOperationId is null)
                throw new JsonException();
            return binding;
        }).ToArray();
        var arm = new FactoryPreparationArm(azure, http, admission.Identity);
        var routes = new List<ReviewedModelRoute>();
        foreach (var route in selection.Routes)
        {
            using var account = await GetAsync(arm, route.AccountResourceId, route.Name, token);
            var observed = FactoryModelsValidation.ObserveAccount(route, account.RootElement);
            using var deployment = await arm.SendAsync(HttpMethod.Get, FactoryModelsValidation.DeploymentTarget(route), FactoryModelsValidation.ProviderApi, token);
            ModelResourceObservation? existing = null;
            if (route.Create)
            {
                if (deployment.StatusCode != HttpStatusCode.NotFound)
                    throw FactoryModelsValidation.Failure(ModelCapabilityReason.UnsupportedTarget, route.Name, "Factory deployment target not proven absent; no adoption, overwrite or retry.");
            }
            else
            {
                RequireRead(deployment, route.Name);
                using var body = await DocumentAsync(deployment, token);
                existing = FactoryModelsValidation.ObserveDeployment(route, body.RootElement);
            }
            routes.Add(new(route, observed, existing));
        }
        var configuration = new FactoryModelsConfiguration(1, admission.Identity.FactoryId, admission.Identity.PlanFingerprint,
            admission.Foundation.ProductEndpoint!, admission.Foundation.ProfilePin!, selection.RuntimeImage, selection.AudienceClientId,
            selection.ValidForSeconds, routes.Select(FactoryModelsValidation.Hash).ToArray(), bindings.Select(FactoryModelsValidation.Hash).ToArray())
        { HostTemplateSha256 = FactoryReservationRecords.Hash(File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-models.json"))) };
        var configScope = bindings[0].Target[..bindings[0].Target.IndexOf("/providers/", StringComparison.Ordinal)]
            + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + admission.Identity.FactoryId.ToString("N");
        var grants = new List<ModelAccessGrant>();
        foreach (var (scope, role) in new[] { (configScope, "/subscriptions/" + admission.Identity.SubscriptionId
                     + "/providers/microsoft.authorization/roledefinitions/" + FactoryModelsValidation.ConfigReaderRole) }
            .Concat(routes.Select(r => (r.Selection.AccountResourceId, r.Selection.InferenceRoleDefinitionId)).Distinct()))
        {
            using var response = await arm.SendAsync(HttpMethod.Get, role, "2022-04-01", token);
            RequireRead(response, "inference-role");
            using var native = await DocumentAsync(response, token);
            grants.Add(FactoryModelsValidation.Grant(scope, role, bindings[0].PrincipalId!,
                FactoryModelsValidation.ObserveRole(role, native.RootElement)));
        }
        configuration = configuration with { GrantHashes = grants.Select(FactoryModelsValidation.Hash).ToArray() };
        var preview = new FactoryModels(Guid.Empty, FactoryModelsValidation.Hash(configuration), configuration, ModelCapabilityState.Partial, "preview")
        { Routes = routes, Bindings = bindings, Grants = grants, Detail = "Approve only factory synthesis/embedding creation, reviewed inference/config grants and hosted workload proof. Existing jurors/model accounts are reused, not owned. Not council readiness." };
        CheckCapacity(preview);
        return preview;
    }

    private static async Task<JsonDocument> GetAsync(FactoryPreparationArm arm, string target, string route, CancellationToken token)
    {
        using var response = await arm.SendAsync(HttpMethod.Get, target, FactoryModelsValidation.ProviderApi, token);
        RequireRead(response, route);
        return await DocumentAsync(response, token);
    }
    private static void RequireRead(HttpResponseMessage response, string route)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw FactoryModelsValidation.Failure(response.StatusCode switch
            {
                HttpStatusCode.NotFound => ModelCapabilityReason.MissingDeployment,
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => ModelCapabilityReason.PermissionDenied,
                _ => ModelCapabilityReason.UnsupportedTarget,
            }, route, "Current native model resource metadata unavailable; model-serving/RBAC administrator must reconcile. No substitute or repair.");
    }
    private static async Task<JsonDocument> DocumentAsync(HttpResponseMessage response, CancellationToken token) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(token), new JsonDocumentOptions { AllowDuplicateProperties = false });

    private static void CheckCapacity(FactoryModels preview)
    {
        var id = Guid.Empty;
        var hash = new string('f', 64);
        var count = preview.Grants!.Count + 4;
        var maximal = FactoryModelsValidation.Persisted(preview) with
        {
            OperationId = id, State = ModelCapabilityState.Verified, Stage = "preparation", Detail = new string('x', 1024),
            OperationCount = count, EffectHashes = Enumerable.Repeat(hash, count).ToArray(), HostEndpoint = new string('x', 261),
            CheckId = id, LastProofReference = ProductConfigurationKeys.ModelProof(id, id), LastProofSha256 = hash,
            VerifiedUntil = DateTimeOffset.MaxValue,
        };
        // Upper envelopes are capacity calculations only, never persisted or sent as workload proof.
        Check(ProductConfigurationKeys.FactoryModels, FactoryReservationRecords.Serialize(maximal), 0);
        var observations = FactoryModelsValidation.RouteNames.Select(name => new ModelWorkloadObservation(name, hash,
            ModelCapabilityState.Verified, ModelCapabilityReason.NetworkUnavailable, new string('x', 141), id.ToString(),
            hash, 8192, 3072, [new[] { "three", "three", "three" }, new[] { "three", "three", "three" }, new[] { "three", "three", "three" }])).ToArray();
        var proof = new ModelCapabilityProof(preview.Configuration.FactoryId, id, id, hash, id.ToString(),
            DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, ModelCapabilityState.Verified, observations);
        Check(ProductConfigurationKeys.ModelProof(id, id), FactoryReservationRecords.Serialize(proof), 0);
        for (var i = 0; i < 5; i++)
            Check(ProductConfigurationKeys.ModelRoute(id, i), FactoryReservationRecords.Serialize(preview.Routes![i]), 0);
        for (var i = 0; i < 3; i++)
            Check(ProductConfigurationKeys.ModelBinding(id, FactoryModelsValidation.BindingNames[i]), FactoryReservationRecords.Serialize(preview.Bindings![i]), 0);
        for (var i = 0; i < preview.Grants.Count; i++)
            Check(ProductConfigurationKeys.ModelGrant(id, i), FactoryReservationRecords.Serialize(preview.Grants[i]), 0);
        foreach (var target in preview.Routes!.Take(2).Select(r => FactoryModelsValidation.DeploymentTarget(r.Selection))
            .Concat(preview.Grants.Select(g => g.Target)).Concat([FactoryModelsValidation.HostTarget(preview) + "/authconfigs/current"]))
        {
            var receipt = new FoundationOperation(1, FactoryModelsValidation.OperationKind, preview.Configuration.FactoryId,
                id, count, target, "2024-10-01", hash, id, DateTimeOffset.MaxValue, FoundationOperationState.Completed, FoundationResourceDisposition.CreateRequested)
            {
                ResourceId = target, NativeRevision = hash, Incarnation = DateTimeOffset.MaxValue.ToString("O"), HttpStatus = 599,
                ProviderRequestId = id.ToString(), ProviderCorrelationId = id.ToString(), ProviderOperationId = new string('f', 128),
                DeploymentTarget = target, PrincipalId = id.ToString(), LogicalIdentity = "factory:council", ConditionVersion = "2.0",
                RoleDefinitionId = preview.Grants.MaxBy(g => g.RoleDefinitionId.Length)!.RoleDefinitionId, RoleScope = target,
            };
            Check(ProductConfigurationKeys.ModelOperation(id, count), FactoryReservationRecords.Serialize(receipt), 0);
        }
        static void Check(string key, string json, int reserved)
        {
            if (OwnerReservationStore.RecordSizeBytes(key, ProductConfigurationKeys.NoLabel, json) + reserved > OwnerReservationStore.RecordLimit)
                throw new ReservationStoreException(FactoryReservationState.Failed, "Model lifecycle exceeds 10 KiB record capacity before intent; no truncation or effects.");
        }
    }
}
