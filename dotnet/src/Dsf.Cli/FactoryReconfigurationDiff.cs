using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryReconfigurationService
{
    private static readonly JsonSerializerOptions DiffJson = new(FactoryGitHubJson.Options) { IgnoreReadOnlyProperties = false };

    private static IReadOnlyList<FactoryConfigurationChange> Changes(FactoryReservationResult admission,
        FactoryReconfigurationInputs current, FactoryReconfigurationInputs desired, ApplicationBoundaryPlan plan,
        FactoryTelemetry telemetry, FactorySupplemental? supplemental, IReadOnlyList<FoundationOperation> history)
    {
        var changes = new List<FactoryConfigurationChange>();
        Set("application", current.Boundary.ApplicationResourceIds, desired.Boundary.ApplicationResourceIds, id => id);
        Set("backend", current.Boundary.EvidenceBackends, desired.Boundary.EvidenceBackends, b => b.ResourceId);
        Set("claim", admission.Plan!.OwnerAdmission!.ProposedClaims, plan.OwnerAdmission!.ProposedClaims, c => c.Key);
        Set("model", current.Models.Routes, desired.Models.Routes, r => r.Name);
        Set("source", current.Supplemental?.Sources ?? [], desired.Supplemental?.Sources ?? [], s => s.Kind);
        One("source", "code", current.Source, desired.Source);
        One("model", "host", new { current.Models.RuntimeImage, current.Models.AudienceClientId, current.Models.ValidForSeconds },
            new { desired.Models.RuntimeImage, desired.Models.AudienceClientId, desired.Models.ValidForSeconds });
        One("source", "supplemental-host", current.Supplemental is null ? null : new { current.Supplemental.RuntimeImage, current.Supplemental.AudienceClientId },
            desired.Supplemental is null ? null : new { desired.Supplemental.RuntimeImage, desired.Supplemental.AudienceClientId });
        One("telemetry", "host", new { current.Telemetry.RuntimeImage, current.Telemetry.AudienceClientId },
            new { desired.Telemetry.RuntimeImage, desired.Telemetry.AudienceClientId });
        One("telemetry", "freshnessSeconds", current.Telemetry.FreshnessSeconds, desired.Telemetry.FreshnessSeconds);
        One("telemetry", "queryRevision", current.Telemetry.QueryRevision, desired.Telemetry.QueryRevision);
        Set("query", current.Telemetry.Queries, desired.Telemetry.Queries, p => p.Key);
        Set("stale-query", current.Telemetry.StaleQueries, desired.Telemetry.StaleQueries, p => p.Key);
        One("judgment", "configuration", current.Judgment, desired.Judgment);
        One("repository", "automation", new { current.Boundary.AutomationOwner, current.Boundary.AutomationDeclarations },
            new { desired.Boundary.AutomationOwner, desired.Boundary.AutomationDeclarations });
        One("boundary", "dedication", current.Boundary.BoundaryReview is { } beforeReview
                ? new { beforeReview.AccountableOwner, beforeReview.Dedicated, beforeReview.CrossOwnerExclusive } : null,
            desired.Boundary.BoundaryReview is { } afterReview
                ? new { afterReview.AccountableOwner, afterReview.Dedicated, afterReview.CrossOwnerExclusive } : null);
        var previous = admission.Plan.RepositoryReview.Repository!;
        var repository = plan.RepositoryReview.Repository!;
        One("repository", "metadata", new { previous.Owner, previous.Name, previous.DefaultBranch },
            new { repository.Owner, repository.Name, repository.DefaultBranch }, metadata: true);
        Set("model-prerequisite", current.Boundary.ModelResourceIds, desired.Boundary.ModelResourceIds, id => id);
        var targetBackends = desired.Boundary.EvidenceBackends.Select(b => new TelemetryBackend(b.ResourceId,
            plan.Azure!.Resources.Single(r => r.Id == b.ResourceId).WorkspaceId!, b.Tables,
            plan.Azure.Resources.Where(r => r.Type == "microsoft.insights/components" && r.BackingWorkspaceResourceId == b.ResourceId
                && desired.Boundary.ApplicationResourceIds.Contains(r.Id, StringComparer.Ordinal)).Select(r => r.Id).Order(StringComparer.Ordinal).ToArray())).ToArray();
        var targetTelemetry = telemetry.Configuration with { Backends = targetBackends, ApplicationResourceIds = desired.Boundary.ApplicationResourceIds };
        Set("grant", telemetry.Configuration.AccessGrants.Select(TelemetryGrant),
            FactoryTelemetryValidation.Grants(targetTelemetry, admission.Identity!.SubscriptionId).Select(TelemetryGrant), g => g.Target);
        Set("grant", ModelGrants(current), ModelGrants(desired), g => g.Target);
        Set("grant", SupplementalGrants(current.Supplemental, supplemental?.Configuration), SupplementalGrants(desired.Supplemental, null), g => g.Target);
        return changes.OrderBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Target, StringComparer.Ordinal).ToArray();

        IEnumerable<ReviewedGrant> ModelGrants(FactoryReconfigurationInputs input) => input.Models.Routes
            .Select(r => (r.AccountResourceId, r.InferenceRoleDefinitionId)).Distinct().Select(pair =>
            {
                var council = admission.Models!.Bindings!.Single(b => b.LogicalIdentity == "factory:council");
                var grant = FactoryModelsValidation.Grant(pair.AccountResourceId, pair.InferenceRoleDefinitionId, council.PrincipalId!, "");
                return new ReviewedGrant(grant.Target, grant.PrincipalId, council.Target, grant.Scope, grant.RoleDefinitionId, null, null);
            });
        IEnumerable<ReviewedGrant> SupplementalGrants(SupplementalSelection? input, SupplementalConfiguration? retained)
        {
            if (input is null) return [];
            var config = retained ?? FactorySupplementalService.Preview(admission, input).Configuration!;
            return input.Sources.SelectMany(source =>
            {
                var identityTarget = FactorySupplementalValidation.IdentityTarget(config, source.Kind);
                var principal = history.FirstOrDefault(o => o.Target == identityTarget)?.PrincipalId;
                return FactorySupplementalValidation.Grants(config, source).Select(g =>
                    new ReviewedGrant(g.Target, principal, identityTarget, g.Scope, g.RoleDefinitionId, null, null));
            });
        }
        ReviewedGrant TelemetryGrant(TelemetryAccessGrant grant) => new(grant.Target, grant.PrincipalId,
            telemetry.Configuration.SourceIdentity.Target, grant.Scope, grant.RoleDefinitionId, null, null);
        void Set<T>(string category, IEnumerable<T> old, IEnumerable<T> next, Func<T, string> key)
        {
            var before = old.ToDictionary(key, StringComparer.Ordinal);
            var after = next.ToDictionary(key, StringComparer.Ordinal);
            foreach (var target in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var hasBefore = before.TryGetValue(target, out var left);
                var hasAfter = after.TryGetValue(target, out var right);
                Add(category, target, hasBefore ? Element(left) : null, hasAfter ? Element(right) : null, metadata: false);
            }
        }
        void One<T>(string category, string target, T before, T after, bool metadata = false) =>
            Add(category, target, before is null ? null : Element(before), after is null ? null : Element(after), metadata);
        void Add(string category, string target, JsonElement? before, JsonElement? after, bool metadata)
        {
            if (before is null && after is null || before is not null && after is not null
                && FactoryModelsValidation.Hash(before.Value) == FactoryModelsValidation.Hash(after.Value)) return;
            changes.Add(new(category, target, metadata ? FactoryConfigurationChangeKind.Metadata
                : before is null ? FactoryConfigurationChangeKind.Addition : after is null ? FactoryConfigurationChangeKind.Removal
                : FactoryConfigurationChangeKind.Replacement, before, after));
        }
        static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, DiffJson);
    }

    private sealed record ReviewedGrant(string Target, string? PrincipalId, string IdentityTarget,
        string Scope, string RoleDefinitionId, string? Condition, string? ConditionVersion);
}
