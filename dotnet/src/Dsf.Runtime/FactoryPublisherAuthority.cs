using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Runtime;

internal sealed record PublisherAuthoritySnapshot(FactoryPublisherCapability Capability, FactoryManifest Manifest,
    IReadOnlyDictionary<string, ConfigurationSetting> Records, string Revision, TokenCredential Credential)
{
    internal AuditRecordIdentity Identity(string key)
    {
        var record = Records[key];
        return new(Manifest.Identity.FactoryId, AuditStoreKind.Governance, key, record.ETag.ToString(), FactoryAudit.TextHash(record.Value));
    }
}

internal sealed class FactoryPublisherAuthority(FactoryHostSettings settings, bool contextHost,
    TokenCredential credential, HttpClient http, TimeProvider clock, FactoryInspectionAuthority? inspection = null)
{
    internal async Task<PublisherAuthoritySnapshot> CheckAsync(CancellationToken token)
    {
        var records = await ReadAsync(token);
        var values = records.ToDictionary(p => p.Key, p => p.Value.Value);
        var manifest = FactoryGovernance.ReadInactiveManifest(values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
            values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile), settings.Product, settings.ProductEndpoint, settings.ProfilePin);
        if (!values.ContainsKey(FactoryPublisher.RootKey)) throw Denied("ReviewedNativePublicationCapabilityRequired");
        var capability = FactoryPublisher.ReadConfirmed(values.Where(p => p.Key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal)).ToDictionary(), clock.GetUtcNow());
        var plan = capability.Plan;
        var factory = manifest.Identity;
        if (plan.Deployment.Manifest.Identity != factory || plan.Deployment.Manifest.ProfilePin != manifest.ProfilePin)
            throw Denied("OriginalPublicationFactoryBoundaryRequired");
        var revision = FactoryModelsValidation.Hash(records.Values.Where(r => !r.Key.StartsWith(FactoryAudit.Namespace, StringComparison.Ordinal))
            .OrderBy(r => r.Key, StringComparer.Ordinal).Select(Identity).ToArray());
        if (revision != plan.Activation.AuthorityRevision) throw Denied("ReviewedPublicationAuthorityChanged");
        foreach (var reference in new[] { plan.Approval, plan.Live, plan.GitHub })
            if (!records.TryGetValue(reference.Key, out var original) || Identity(original) != reference)
                throw Denied("OriginalPublicationApprovalReferenceChanged");
        if (capability.Coordinator.NativeProviderProof is { } proof
            && (!records.TryGetValue(proof.Key, out var observedProof) || Identity(observedProof) != proof))
            throw Denied("OriginalAuthenticatedPublisherProofChanged");
        var approval = FactoryActivation.Read(values.Where(p => p.Key.StartsWith(FactoryActivation.Prefix, StringComparison.Ordinal)).ToDictionary(),
            factory.FactoryId, clock.GetUtcNow());
        if (approval.Operation.Phase != FactoryActivationPhase.ApprovalOnly || approval.Review?.Fingerprint != plan.Activation.Fingerprint
            || approval.Approval is null) throw Denied("OriginalReviewedApprovalRequired");
        var own = plan.Deployment.Bindings[contextHost ? "council" : "auth"];
        if (settings.ManagedIdentityClientId != own.ClientId) throw Denied("ExactPublicationWorkloadIdentityRequired");
        var workload = new FactoryWorkloadCredential(credential, factory.TenantId, own, clock);
        var native = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in new[] { "auth", "council", "hosting", "account" })
        {
            var original = plan.Deployment.Bindings[binding];
            var resource = await NativeAsync(original.Target, original.ApiVersion, workload, token);
            var incarnation = FactorySourceValidation.InfrastructureIncarnation(resource, original.Target);
            if (DateTimeOffset.TryParse(original.Incarnation, System.Globalization.CultureInfo.InvariantCulture, out var created)
                ? !DateTimeOffset.TryParse(incarnation, System.Globalization.CultureInfo.InvariantCulture, out var current) || created != current
                : incarnation != original.Incarnation && (!resource.TryGetProperty("etag", out var tag) || tag.GetString() != original.Incarnation))
                throw Denied("OriginalPublicationBindingIncarnationChanged");
            var p = resource.GetProperty("properties");
            if (original.PrincipalId is not null && (p.GetProperty("principalId").GetString() != original.PrincipalId
                || p.GetProperty("clientId").GetString() != original.ClientId || p.GetProperty("tenantId").GetString() != factory.TenantId))
                throw Denied("OriginalPublicationIdentityChanged");
            if (binding == "hosting" && !p.GetProperty("vnetConfiguration").GetProperty("internal").GetBoolean())
                throw Denied("OriginalPrivatePublicationEnvironmentRequired");
            if (binding == "account" && (!p.GetProperty("disableLocalAuth").GetBoolean() || p.GetProperty("publicNetworkAccess").GetString() != "Disabled"
                || FactoryGovernance.NormalizeEndpoint(p.GetProperty("documentEndpoint").GetString()!)
                    != "https://" + FactoryPreviewValidation.Prefix(factory.FactoryId) + "-cosmos.documents.azure.com"))
                throw Denied("OriginalPrivatePublicationStateRequired");
            native.Add(original.Target, resource);
        }
        foreach (var receipt in capability.Receipts)
            native.Add(receipt.Target, await NativeAsync(receipt.Target, receipt.ApiVersion, workload, token));
        var target = plan.Deployment.Steps[0].Target;
        var deployment = await NativeAsync(target, "2025-04-01", workload, token);
        var operations = await NativeAsync(target + "/operations", "2025-04-01", workload, token, collection: true);
        FactoryPublisher.ValidateCreation(capability, native, deployment, operations);
        FactoryPublisher.ValidateResources(plan, native, capability.ContextEndpoint);
        foreach (var step in plan.Deployment.Steps.Skip(1))
        {
            var grant = step.Grant!;
            var receipt = capability.Receipts.Single(r => r.Target == grant.Target);
            var properties = native[grant.Target].GetProperty("properties");
            var created = properties.GetProperty("createdOn").GetDateTimeOffset();
            if (FactoryModelsValidation.Hash(properties) != receipt.NativeRevision
                || created != DateTimeOffset.Parse(receipt.Incarnation, System.Globalization.CultureInfo.InvariantCulture)
                || properties.GetProperty("principalId").GetString() != grant.PrincipalId
                || properties.GetProperty("principalType").GetString() != "ServicePrincipal"
                || !Same(properties.GetProperty("scope").GetString(), grant.Scope)
                || !Same(properties.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId)
                || properties.GetProperty("createdBy").GetString() != factory.AzureActorId
                || properties.TryGetProperty("updatedOn", out var updated) && updated.ValueKind != JsonValueKind.Null && updated.GetDateTimeOffset() != created
                || properties.TryGetProperty("condition", out var condition) && condition.ValueKind != JsonValueKind.Null)
                throw Denied("OriginalPublicationMetadataGrantChanged");
        }
        var roleScope = FactoryPublisher.HostTarget(plan.Deployment, contextHost);
        var role = await NativeAsync(roleScope + "/providers/microsoft.authorization/roledefinitions/" + plan.Selection.MetadataRoleDefinitionId.Split('/')[^1],
            "2022-04-01", workload, token, identity: plan.Selection.MetadataRoleDefinitionId);
        if (FactoryModelsValidation.Hash(role.GetProperty("properties")) != plan.Deployment.Steps[1].Grant!.RoleRevision)
            throw Denied("ExactPublicationMetadataRoleChanged");
        foreach (var scope in plan.Deployment.Steps.Skip(1).Select(s => s.Grant!).Where(g => g.PrincipalId == own.PrincipalId)
            .Select(g => g.Scope).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var assignments = await NativeAsync(scope + "/providers/microsoft.authorization/roleassignments?$filter=atScope()+and+assignedTo('"
                + own.PrincipalId + "')", "2022-04-01", workload, token, collection: true);
            var expectedGrants = plan.Deployment.Steps.Skip(1).Select(s => s.Grant!)
                .Where(g => g.PrincipalId == own.PrincipalId && (Same(g.Scope, scope) || scope.StartsWith(g.Scope + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
            var observedGrants = assignments.GetProperty("value").EnumerateArray().ToArray();
            if (!observedGrants.Select(g => g.GetProperty("id").GetString()!).Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(expectedGrants.Select(g => g.Target).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw Denied("PublicationEffectiveMetadataAuthorityExpandedOrUnconfirmed");
            foreach (var actualGrant in observedGrants)
            {
                var receipt = capability.Receipts.Single(r => Same(r.Target, actualGrant.GetProperty("id").GetString()));
                if (FactoryModelsValidation.Hash(actualGrant.GetProperty("properties")) != receipt.NativeRevision)
                    throw Denied("PublicationEffectiveMetadataGrantChanged");
            }
        }
        var inventory = await NativeAsync(plan.Deployment.Bindings["account"].Target + "/sqlroleassignments", "2024-11-15", workload, token, collection: true);
        var knownPrincipals = plan.Deployment.Bindings.Values.Where(b => b.PrincipalId is not null).Select(b => b.PrincipalId!)
            .Concat(plan.GitHubAuthority.OtherWorkloadPrincipalIds).Append(factory.AzureActorId).ToHashSet(StringComparer.Ordinal);
        if (inventory.GetProperty("value").EnumerateArray().Any(a => !knownPrincipals.Contains(a.GetProperty("properties").GetProperty("principalId").GetString()!)))
            throw Denied("PublicationSqlPrincipalOrGroupAuthorityUnconfirmed");
        var actual = inventory.GetProperty("value").EnumerateArray().Where(a =>
            a.GetProperty("properties").GetProperty("principalId").GetString() == plan.GitHubAuthority.AuthPrincipalId).ToArray();
        var expected = plan.Deployment.Steps[0].ResourceTargets.Single(t => t.Contains("/sqlroleassignments/", StringComparison.Ordinal)
            && native[t].GetProperty("properties").GetProperty("principalId").GetString() == plan.GitHubAuthority.AuthPrincipalId);
        if (actual.Length != 1 || !Same(actual[0].GetProperty("id").GetString(), expected)
            || FactoryModelsValidation.Hash(actual[0].GetProperty("properties")) != capability.Receipts.Single(r => r.Target == expected).NativeRevision)
            throw Denied("AuthPublicationDataScopeExpanded");
        var latest = await ReadAsync(token);
        var governance = Revision(records);
        if (Revision(latest) != governance) throw Denied("PublicationGovernanceChangedDuringAdmission");
        return new(capability, manifest, records, FactoryModelsValidation.Hash(new { Governance = governance,
            Native = native.OrderBy(p => p.Key).Select(p => new { p.Key, Hash = FactoryModelsValidation.Hash(p.Value) }),
            Deployment = FactoryModelsValidation.Hash(deployment), Operations = FactoryModelsValidation.Hash(operations),
            Role = FactoryModelsValidation.Hash(role), Inventory = FactoryModelsValidation.Hash(inventory) }), workload);

        AuditRecordIdentity Identity(ConfigurationSetting record) => new(factory.FactoryId, AuditStoreKind.Governance,
            record.Key, record.ETag.ToString(), FactoryAudit.TextHash(record.Value));
    }
    internal async Task RequireSameAsync(PublisherAuthoritySnapshot snapshot, CancellationToken token)
    {
        if (inspection is not null) throw Denied("OriginalInvocationInspectionCannotAdmitWork");
        if ((await CheckAsync(token)).Revision != snapshot.Revision) throw Denied("PublicationAdmissionAuthorityChanged");
    }
    private async Task<Dictionary<string, ConfigurationSetting>> ReadAsync(CancellationToken token)
    {
        var gateway = new AzureConfigurationSettingsGateway(credential, http);
        foreach (var prefix in new[] { FactoryPause.Key, FactoryPause.HistoryPrefix })
            await foreach (var record in gateway.ListRecordsAsync(settings.ProductEndpoint, "*", token, prefix + "*"))
                if (inspection is not null) _ = inspection.Include(record.Key);
                else FactoryPause.RequireAdmissionOpen([record.Key]);
        var result = new Dictionary<string, ConfigurationSetting>();
        await foreach (var record in gateway.ListRecordsAsync(settings.ProductEndpoint, ProductConfigurationKeys.NoLabel, token))
        {
            if (inspection is not null && !inspection.Include(record.Key)) continue;
            FactoryPause.RequireAdmissionOpen([record.Key]);
            if (result.Count >= FactoryPreviewPublication.Policy.MaximumRecords + FactoryAudit.GovernanceHistoryRecords
                || System.Text.Encoding.UTF8.GetByteCount(record.Key + record.Value) + 512 > FactoryCharterLimits.RecordBytes
                || record.ETag.ToString().Length == 0 || !result.TryAdd(record.Key, record)) throw new JsonException();
        }
        foreach (var prefix in new[] { FactoryPublisher.Prefix, FactoryActivation.Prefix, FactoryLive.Prefix, FactoryPromotion.Prefix })
        {
            var keys = new HashSet<string>();
            await foreach (var record in gateway.ListRecordsAsync(settings.ProductEndpoint, "*", token, prefix + "*"))
                if (record.Label is not null || !keys.Add(record.Key) || !result.TryGetValue(record.Key, out var captured)
                    || captured.Value != record.Value || captured.ETag != record.ETag) throw new JsonException();
            if (!keys.SetEquals(result.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))) throw new JsonException();
        }
        return result;
    }
    private async Task<JsonElement> NativeAsync(string target, string api, TokenCredential workload, CancellationToken token,
        string? identity = null, bool collection = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com" + target
            + (target.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + api);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            (await workload.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token)).Token);
        using var response = await http.SendAsync(request, token);
        if (response.StatusCode != HttpStatusCode.OK) throw Denied("CurrentNativePublicationAuthorityUnavailable");
        using var body = await EvidencePayload.ReadAsync(response, token, 256 * 1024);
        if (!collection && !Same(body.RootElement.GetProperty("id").GetString(), identity ?? target)
            || collection && body.RootElement.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "")
            throw new JsonException();
        return body.RootElement.Clone();
    }
    private static string Revision(IReadOnlyDictionary<string, ConfigurationSetting> records) => FactoryModelsValidation.Hash(
        records.Where(p => FactoryPromotion.IsLiveAuthorityKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new { p.Key, p.Value.Value, ETag = p.Value.ETag.ToString(), p.Value.ContentType }));
    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static PreviewCapabilityException Denied(string reason) => new(FactoryCharterState.Blocked, reason);
}
