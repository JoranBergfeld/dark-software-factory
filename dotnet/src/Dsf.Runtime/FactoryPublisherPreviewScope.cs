using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed partial class NativePreviewBindings
{
    private async Task<JsonElement> OriginalPreviewSqlInventoryAsync(JsonElement inventory, CancellationToken token)
    {
        var values = PublicationGovernance.Where(p => p.Key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value.Value);
        if (!values.TryGetValue(FactoryPublisher.RootKey, out var rootValue)) return inventory;
        var root = JsonSerializer.Deserialize<FactoryPublisherCoordinator>(rootValue, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        var plan = FactoryPublisher.ReadPlan(root, values);
        if (plan.Deployment.Manifest.Identity != manifest.Identity || plan.GitHubAuthority != github
            && FactoryModelsValidation.Hash(plan.GitHubAuthority) != FactoryModelsValidation.Hash(github))
            throw Changed("PublicationExtensionFactoryAuthorityChanged");
        var account = plan.Deployment.Bindings["account"].Target;
        var scope = FactoryPreviewValidation.SqlScope(account, manifest.Identity.ProductKey, FactoryPublication.Container);
        var targets = new[] { true, false }.Select(writer => FactoryPreviewValidation.SqlAssignment(account,
            plan.Deployment.Bindings[writer ? "auth" : "council"].PrincipalId!, scope, FactoryPublisher.Role(plan.Deployment, writer))).ToArray();
        var present = Complete(inventory).Where(a => targets.Contains(a.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (present.Length == 0) return inventory;
        if (present.Length != 2) throw Changed("PublicationExtensionNativeInventoryIncomplete");
        var created = FactoryPublisher.ReadCreatedDeployment(values, clock.GetUtcNow());
        foreach (var reference in new[] { plan.Approval, plan.Live, plan.GitHub })
        {
            if (!PublicationGovernance.TryGetValue(reference.Key, out var record)
                || record.ETag.ToString() != reference.NativeRevision || FactoryAudit.TextHash(record.Value) != reference.Sha256)
                throw Changed("PublicationExtensionOriginalApprovalChanged");
        }
        for (var i = 0; i < 2; i++)
        {
            var writer = i == 0;
            var assignment = present.Single(a => Same(a.GetProperty("id").GetString(), targets[i]));
            var receipt = created.Receipts.Single(r => r.Target == targets[i]);
            var properties = assignment.GetProperty("properties");
            var roleTarget = FactoryPublisher.Role(plan.Deployment, writer);
            if (FactoryModelsValidation.Hash(properties) != receipt.NativeRevision || receipt.Incarnation != "properties-sha256:" + receipt.NativeRevision
                || properties.GetProperty("principalId").GetString() != plan.Deployment.Bindings[writer ? "auth" : "council"].PrincipalId
                || !Same(properties.GetProperty("scope").GetString(), scope) || !Same(properties.GetProperty("roleDefinitionId").GetString(), roleTarget))
                throw Changed("PublicationExtensionNativeAssignmentChanged");
            var role = await GetAsync(roleTarget, "2024-11-15", token);
            Revisions.Remove(roleTarget);
            var definition = role.GetProperty("properties");
            receipt = created.Receipts.Single(r => r.Target == roleTarget);
            if (FactoryModelsValidation.Hash(definition) != receipt.NativeRevision
                || receipt.Incarnation != "properties-sha256:" + receipt.NativeRevision
                || definition.GetProperty("type").GetString() != "CustomRole" || definition.GetProperty("permissions").GetArrayLength() != 1)
                throw Changed("PublicationExtensionNativeRoleChanged");
            Exact(definition, "assignableScopes", [account]);
            Exact(definition.GetProperty("permissions")[0], "dataActions", writer ? FactoryPublisher.WriteDataActions : FactoryPublisher.ReadDataActions);
        }
        // Preserve the original preview's exact scope fingerprint, not a whole-account inventory fingerprint.
        // Only this separately reviewed, acknowledged, unrelated container capability is removed from that projection.
        var original = JsonNode.Parse(inventory.GetRawText())!;
        var entries = original["value"]!.AsArray();
        for (var i = entries.Count - 1; i >= 0; i--)
            if (targets.Contains((string)entries[i]!["id"]!, StringComparer.OrdinalIgnoreCase)) entries.RemoveAt(i);
        var projected = JsonSerializer.SerializeToElement(original);
        Revisions[Account + "/sqlroleassignments"] = FactoryModelsValidation.Hash(projected);
        return projected;
    }
}
