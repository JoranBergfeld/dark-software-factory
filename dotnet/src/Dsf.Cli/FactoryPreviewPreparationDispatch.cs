using System.Net;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryPreviewPreparationService
{
    private sealed record NativeCreation(IReadOnlyList<PreviewPreparationReceipt> Receipts, string? CouncilEndpoint, string? OutputsSha256);

    private async Task<NativeCreation> DispatchAsync(PreviewPreparationPlan plan, PreviewPreparationStep step,
        PreviewPreparationEffect effect, FactoryPreparationArm arm, Func<PreviewPreparationAcknowledgment, Task> recordAcknowledgment, CancellationToken token)
    {
        var body = step.Action == PreviewPreparationAction.WorkloadDeployment ? plan.WorkloadBody : FactoryPreviewPreparation.GrantBody(step.Grant!);
        if (FactoryReservationRecords.Hash(body) != step.BodySha256) throw Unknown("ApprovedNativeRequestBodyChanged");
        using var response = await arm.SendAsync(HttpMethod.Put, step.Target, step.ApiVersion, token, body, effect.RequestId);
        var acknowledgment = new PreviewPreparationAcknowledgment((int)response.StatusCode, OptionalGuidHeader(response, "x-ms-request-id"),
            OptionalGuidHeader(response, "x-ms-correlation-request-id"), clock.GetUtcNow(), null, null, null);
        await recordAcknowledgment(acknowledgment);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.RequestEntityTooLarge) throw new NativePreparationRejectedException();
        if (response.StatusCode != HttpStatusCode.Created) throw Unknown("NativeCreationAcknowledgmentAmbiguous");
        var request = GuidHeader(response, "x-ms-request-id");
        var correlation = GuidHeader(response, "x-ms-correlation-request-id");
        var native = await BodyAsync(response, token);
        acknowledgment = acknowledgment with { BodySha256 = FactoryReservationRecords.Hash(native.GetRawText()) };
        await recordAcknowledgment(acknowledgment);
        if (!Same(native.GetProperty("id").GetString(), step.Target)) throw Unknown("NativeCreatedTargetChanged");
        if (step.Action == PreviewPreparationAction.MetadataAssignment)
        {
            RequireGrant(native, step.Grant!, plan.Manifest.Identity.AzureActorId);
            var created = native.GetProperty("properties").GetProperty("createdOn").GetDateTimeOffset();
            if (created < effect.IntendedAt || created > clock.GetUtcNow()) throw Unknown("NativeGrantCreationWindowUnconfirmed");
            return new([new(step.Target, step.ApiVersion, created.ToUniversalTime().ToString("O"),
                FactoryModelsValidation.Hash(native.GetProperty("properties")), 201, request, correlation, null, null)], null, null);
        }
        var deploymentCorrelation = native.GetProperty("properties").GetProperty("correlationId").GetString();
        if (!Guid.TryParse(deploymentCorrelation, out var id) || id == Guid.Empty) throw Unknown("NativeDeploymentCorrelationRequired");
        await ObserveDeploymentAsync();
        for (var poll = 0; native.GetProperty("properties").GetProperty("provisioningState").GetString() is "Accepted" or "Running" or "Creating"; poll++)
        {
            if (poll >= 120) throw Unknown("NativeDeploymentPollingBudgetExceeded; remote work may continue.");
            await Task.Delay(TimeSpan.FromSeconds(10), clock, token);
            native = await ReadNativeAsync(arm, step.Target, step.ApiVersion, token);
            if (!Same(native.GetProperty("id").GetString(), step.Target)
                || native.GetProperty("properties").GetProperty("correlationId").GetString() != deploymentCorrelation)
                throw Unknown("NativeDeploymentCorrelationChanged");
            await ObserveDeploymentAsync();
        }
        if (native.GetProperty("properties").GetProperty("provisioningState").GetString() != "Succeeded")
            throw Unknown("NativeDeploymentPartialOrFailed; created resources retained, no redeployment.");
        var properties = native.GetProperty("properties");
        var outputs = properties.GetProperty("outputs");
        var endpoint = DeploymentEndpoint(outputs);
        var resources = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in step.ResourceTargets)
            resources.Add(target, await ReadNativeAsync(arm, target, TargetApi(target), token));
        ValidateWorkloads(plan, resources, endpoint);
        var operations = Complete(await ReadNativeAsync(arm, step.Target + "/operations", step.ApiVersion, token));
        ValidateOperationSet(step, operations);
        var receipts = new List<PreviewPreparationReceipt>();
        foreach (var target in step.ResourceTargets)
        {
            var operation = operations.Single(o => o.GetProperty("properties").GetProperty("provisioningOperation").GetString() == "Create"
                && Same(o.GetProperty("properties").GetProperty("targetResource").GetProperty("id").GetString(), target));
            var receipt = OperationReceipt(step, operation, resources[target], deploymentCorrelation!, effect.IntendedAt);
            if (target.Contains("/microsoft.app/containerapps/", StringComparison.Ordinal) && !target.EndsWith("/authconfigs/current", StringComparison.Ordinal))
                receipt = receipt with { Endpoint = FactoryGovernance.NormalizeEndpoint("https://" + resources[target]
                    .GetProperty("properties").GetProperty("configuration").GetProperty("ingress").GetProperty("fqdn").GetString()) };
            receipts.Add(receipt);
        }
        return new(receipts, endpoint, FactoryModelsValidation.Hash(outputs));

        async Task ObserveDeploymentAsync()
        {
            var state = native.GetProperty("properties").GetProperty("provisioningState").GetString();
            if (state is not ("Accepted" or "Running" or "Creating" or "Succeeded" or "Failed" or "Canceled"))
                throw Unknown("NativeDeploymentStateUnsupported");
            acknowledgment = acknowledgment with { BodySha256 = FactoryReservationRecords.Hash(native.GetRawText()),
                DeploymentCorrelationId = deploymentCorrelation, ProvisioningState = state };
            await recordAcknowledgment(acknowledgment);
        }
    }

    private async Task VerifyConfirmedAsync(Journal journal, CancellationToken token)
    {
        var plan = journal.Plan;
        var arm = new FactoryPreparationArm(azure, http, plan.Manifest.Identity);
        foreach (var effect in journal.Effects.Where(e => e.State == PreviewPreparationEffectState.Confirmed))
        {
            var step = plan.Steps[effect.Index];
            var receipts = journal.Receipts.Where(r => step.ResourceTargets.Contains(r.Target, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (receipts.Length != step.ResourceTargets.Count) throw Unknown("NativePreparationReceiptSetIncomplete");
            if (step.Action == PreviewPreparationAction.MetadataAssignment)
            {
                var resource = await ReadNativeAsync(arm, step.Target, step.ApiVersion, token);
                RequireGrant(resource, step.Grant!, plan.Manifest.Identity.AzureActorId, receipts[0].Incarnation);
                if (FactoryModelsValidation.Hash(resource.GetProperty("properties")) != receipts[0].NativeRevision)
                    throw Failure("ConfirmedNativeMetadataGrantChanged");
                continue;
            }
            var deployment = await ReadNativeAsync(arm, step.Target, step.ApiVersion, token);
            var properties = deployment.GetProperty("properties");
            if (!Same(deployment.GetProperty("id").GetString(), step.Target) || properties.GetProperty("provisioningState").GetString() != "Succeeded"
                || properties.GetProperty("correlationId").GetString() != receipts[0].CorrelationId
                || effect.DeploymentOutputsSha256 != FactoryModelsValidation.Hash(properties.GetProperty("outputs"))
                || effect.CouncilEndpoint != DeploymentEndpoint(properties.GetProperty("outputs")))
                throw Failure("ConfirmedNativeDeploymentChanged");
            var operations = Complete(await ReadNativeAsync(arm, step.Target + "/operations", step.ApiVersion, token));
            ValidateOperationSet(step, operations);
            var resources = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var receipt in receipts)
            {
                var resource = await ReadNativeAsync(arm, receipt.Target, receipt.ApiVersion, token);
                resources.Add(receipt.Target, resource);
                var operation = operations.Single(o => o.GetProperty("operationId").GetString() == receipt.OperationId);
                var observed = OperationReceipt(step, operation, resource, receipt.CorrelationId!, effect.IntendedAt);
                if (observed != receipt with { Endpoint = null }) throw Failure("ConfirmedNativeResourceOrCreationReceiptChanged");
            }
            ValidateWorkloads(plan, resources, effect.CouncilEndpoint!);
        }
    }

    private PreviewPreparationReceipt OperationReceipt(PreviewPreparationStep step, JsonElement operation, JsonElement resource,
        string correlation, DateTimeOffset intended)
    {
        var p = operation.GetProperty("properties");
        var target = p.GetProperty("targetResource").GetProperty("id").GetString()!;
        var operationId = operation.GetProperty("operationId").GetString();
        var status = p.GetProperty("statusCode").GetString() switch { "Created" or "201" => 201, "OK" or "200" => 200, _ => 0 };
        if (!step.ResourceTargets.Contains(target, StringComparer.OrdinalIgnoreCase) || !Same(resource.GetProperty("id").GetString(), target)
            || !FactoryTelemetryValidation.IsProviderOperationId(operationId) || status == 0
            || p.GetProperty("provisioningOperation").GetString() != "Create" || p.GetProperty("provisioningState").GetString() != "Succeeded"
            || !Guid.TryParse(p.GetProperty("trackingId").GetString(), out var tracking) || tracking == Guid.Empty)
            throw Unknown("ExactNativeDeploymentCreateReceiptRequired");
        string incarnation, revision;
        if (target.Contains("/microsoft.app/containerapps/", StringComparison.OrdinalIgnoreCase))
        {
            var observed = FactoryHostedWorkloadPolicy.ObserveExactHost(resource, target);
            incarnation = observed.Incarnation;
            revision = observed.Revision;
            var created = DateTimeOffset.Parse(incarnation, System.Globalization.CultureInfo.InvariantCulture);
            if (created < intended || created > clock.GetUtcNow()) throw Unknown("NativeHostCreationWindowUnconfirmed");
        }
        else
        {
            incarnation = target.Contains("/sqlrole", StringComparison.OrdinalIgnoreCase)
                ? "properties-sha256:" + FactoryModelsValidation.Hash(resource.GetProperty("properties")) : Incarnation(resource);
            revision = FactoryModelsValidation.Hash(resource.GetProperty("properties"));
        }
        return new(target.ToLowerInvariant(), TargetApi(target.ToLowerInvariant()), incarnation, revision, status,
            tracking.ToString(), correlation, step.Target, operationId);
    }

    private static void ValidateOperationSet(PreviewPreparationStep step, JsonElement[] operations)
    {
        var created = operations.Where(o => o.GetProperty("properties").GetProperty("provisioningOperation").GetString() == "Create").ToArray();
        if (created.Length != step.ResourceTargets.Count || !created.Select(o => o.GetProperty("properties").GetProperty("targetResource").GetProperty("id").GetString()!)
                .Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(step.ResourceTargets.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            || operations.Select(o => o.GetProperty("operationId").GetString()).Distinct().Count() != operations.Length
            || operations.Any(o => o.GetProperty("properties").GetProperty("provisioningOperation").GetString() is not ("Create" or "Read" or "EvaluateDeploymentOutput")))
            throw Unknown("NativeDeploymentResourceSetIncompleteOrExpanded");
    }

    private static string GuidHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values) || values.Count() != 1
            || !Guid.TryParse(values.Single(), out var id) || id == Guid.Empty) throw Unknown("NativeCreationRequestReceiptMissing");
        return id.ToString();
    }

    private static string? OptionalGuidHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.Count() == 1
            && Guid.TryParse(values.Single(), out var id) && id != Guid.Empty ? id.ToString() : null;

    private static string DeploymentEndpoint(JsonElement outputs)
    {
        if (outputs.EnumerateObject().Count() != 1 || outputs.GetProperty("councilEndpoint").GetProperty("type").GetString() != "String")
            throw Unknown("ExactNativePreviewDeploymentOutputsRequired");
        return FactoryGovernance.NormalizeEndpoint(outputs.GetProperty("councilEndpoint").GetProperty("value").GetString()!);
    }

    private static void ValidateWorkloads(PreviewPreparationPlan plan, IReadOnlyDictionary<string, JsonElement> resources, string councilEndpoint)
    {
        var account = plan.Bindings["account"].Target;
        var identity = plan.Manifest.Identity;
        var group = account[..account.IndexOf("/providers/", StringComparison.Ordinal)];
        RequireContainer(resources[FactoryPreviewValidation.Container(account, identity.ProductKey, FactoryPreviewValidation.PreviewContainer)], FactoryPreviewValidation.PreviewContainer);
        var role = FactoryPreviewValidation.SqlRole(account, identity.FactoryId);
        var definition = resources[role].GetProperty("properties");
        if (definition.GetProperty("type").GetString() != "CustomRole" || definition.GetProperty("permissions").GetArrayLength() != 1
            || !FactoryModelsValidation.Strings(definition, "assignableScopes").SequenceEqual([account], StringComparer.OrdinalIgnoreCase)
            || !FactoryModelsValidation.Strings(definition.GetProperty("permissions")[0], "dataActions").Order(StringComparer.Ordinal)
                .SequenceEqual(FactoryPreviewValidation.StateDataActions.Order(StringComparer.Ordinal)))
            throw Unknown("ExactPreviewStateRoleRequired");
        foreach (var name in new[] { "runs", FactoryPreviewValidation.PreviewContainer })
        {
            var scope = FactoryPreviewValidation.SqlScope(account, identity.ProductKey, name);
            var target = FactoryPreviewValidation.SqlAssignment(account, plan.Bindings["council"].PrincipalId!, scope, role);
            var properties = resources[target].GetProperty("properties");
            if (properties.GetProperty("principalId").GetString() != plan.Bindings["council"].PrincipalId
                || !Same(properties.GetProperty("roleDefinitionId").GetString(), role) || !Same(properties.GetProperty("scope").GetString(), scope))
                throw Unknown("ExactPreviewStateContainerGrantRequired");
        }
        using var body = JsonDocument.Parse(plan.WorkloadBody);
        var configuration = plan.Configuration with { Supplemental = plan.SupplementalConfiguration };
        foreach (var kind in FactoryPreviewValidation.HostKinds(configuration))
        {
            var optional = configuration.Supplemental?.Hosts.SingleOrDefault(h => FactoryPreviewSupplementalValidation.HostKind(h.Kind) == kind);
            var target = FactoryPreviewValidation.HostTarget(group, identity.FactoryId, kind);
            var host = resources[target];
            var auth = resources[target + "/authconfigs/current"];
            var binding = plan.Bindings[optional?.Kind ?? (kind == PreviewHostKind.SourceContext ? "code"
                : kind == PreviewHostKind.TelemetryEvidence ? "telemetry" : "council")];
            var image = optional?.RuntimeImage ?? (kind == PreviewHostKind.ModelEvaluation ? plan.Selection.Models.RuntimeImage
                : kind == PreviewHostKind.TelemetryEvidence ? plan.Selection.Telemetry.RuntimeImage : plan.Selection.RuntimeImage);
            var audience = optional?.AudienceClientId ?? (kind == PreviewHostKind.ModelEvaluation ? plan.Selection.Models.AudienceClientId
                : kind == PreviewHostKind.TelemetryEvidence ? plan.Selection.Telemetry.AudienceClientId : plan.Selection.AudienceClientId);
            var verb = kind switch
            {
                PreviewHostKind.Council => "serve-preview", PreviewHostKind.SourceContext => "serve-source-context",
                PreviewHostKind.ModelEvaluation => "serve-preview-models", PreviewHostKind.TelemetryEvidence => "serve-preview-telemetry",
                _ => "serve-preview-supplemental",
            };
            var callers = kind == PreviewHostKind.Council
                ? new[] { identity.AzureActorId }.Concat(configuration.Supplemental?.Hosts.Select(h => h.PrincipalId) ?? []).ToArray()
                : [plan.Bindings["council"].PrincipalId!];
            Dictionary<string, string>? extra = optional is null ? null : new()
            {
                ["DSF_SUPPLEMENTAL_CONFIG"] = body.RootElement.GetProperty("properties").GetProperty("parameters").GetProperty("supplementalSources")
                    .GetProperty("value").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == optional.Kind).GetProperty("workloadConfiguration").GetString()!,
                ["DSF_PREVIEW_COORDINATOR"] = JsonSerializer.Serialize(new PreviewSupplementalCoordinator(councilEndpoint,
                    plan.Configuration.AudienceClientId, plan.Bindings["council"].PrincipalId!, plan.Bindings["council"].ClientId!), FactoryGitHubJson.Options),
            };
            var endpoint = FactoryHostedWorkloadPolicy.Validate(host, auth, binding, plan.Bindings["hosting"], image, verb,
                identity.ProductKey, "https://dsfo-" + identity.FactoryId.ToString("N") + ".azconfig.io", plan.Manifest.ProfilePin,
                identity.TenantId, audience, callers, extra);
            var template = host.GetProperty("properties").GetProperty("template");
            var capacity = template.GetProperty("containers")[0].GetProperty("resources");
            if (capacity.GetProperty("cpu").GetDouble() != 0.25 || capacity.GetProperty("memory").GetString() != "0.5Gi"
                || template.GetProperty("scale").GetProperty("minReplicas").GetInt32() != 1
                || template.GetProperty("scale").GetProperty("maxReplicas").GetInt32() != 1)
                throw Unknown("ExactReviewedPreviewHostCapacityRequired");
            if (kind == PreviewHostKind.Council && endpoint != councilEndpoint) throw Unknown("NativeCouncilEndpointOutputMismatch");
            if (kind != PreviewHostKind.Council && !FactoryModelsValidation.Strings(auth.GetProperty("properties")
                .GetProperty("identityProviders").GetProperty("azureActiveDirectory").GetProperty("validation")
                .GetProperty("defaultAuthorizationPolicy"), "allowedApplications").SequenceEqual([plan.Bindings["council"].ClientId!]))
                throw Unknown("CouncilOnlyNativeConsumerAuthenticationRequired");
        }
    }
}
