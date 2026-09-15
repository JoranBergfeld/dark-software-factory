using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class OwnerAdmissionReader(AzureBoundaryReader azure)
{
    public async Task<OwnerAdmissionSnapshot> InspectAsync(
        ApplicationBoundarySelection selection, ReviewedGitHubRepository? repository, CancellationToken cancellationToken)
    {
        var claims = new List<ProposedObservationClaim>
        {
            new("product", selection.ProductKey, ProductConfigurationKeys.OwnerProductClaim(selection.ProductKey), ProductConfigurationKeys.NoLabel),
        };
        if (repository is not null)
            claims.Add(new("repository", repository.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProductConfigurationKeys.OwnerRepositoryClaim(repository.Id), ProductConfigurationKeys.NoLabel));
        claims.AddRange(selection.ApplicationResourceIds.Concat(selection.EvidenceBackends.Select(b => b.ResourceId))
            .Select(id => id.TrimEnd('/').ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)
            .Select(id => new ProposedObservationClaim("resource", id, ProductConfigurationKeys.OwnerResourceClaim(id), ProductConfigurationKeys.NoLabel)));
        var observations = new List<OwnerAdmissionObservation>();
        var findings = new List<BoundaryPlanFinding>();
        try
        {
            var seen = new Dictionary<(string, string?), OwnerAdmissionObservation>();
            foreach (var filter in new[] { ProductConfigurationKeys.OwnerOnboardingFilter,
                         ProductConfigurationKeys.OwnerIndexGitHubRepository, ProductConfigurationKeys.OwnerIndexAppConfigEndpoint })
            {
                var body = await azure.ReadAsync(["appconfig", "kv", "list",
                    "--endpoint", selection.OwnerAppConfigEndpoint, "--auth-mode", "login", "--key", filter,
                    "--label", ProductConfigurationKeys.AnyLabel, "--all", "--subscription", selection.SubscriptionId], cancellationToken);
                foreach (var entry in AzureBoundaryReader.Parse("owner admission", () => body.EnumerateArray().ToArray()))
                {
                    var key = AzureBoundaryReader.Parse("owner admission key", () => AzureBoundaryReader.Text(entry, "key"));
                    var label = AzureBoundaryReader.Parse("owner admission label", () => entry.GetProperty("label").GetString());
                    var value = AzureBoundaryReader.Parse("owner admission value", () => AzureBoundaryReader.Text(entry, "value"));
                    var etag = AzureBoundaryReader.Parse("owner admission revision", () => AzureBoundaryReader.Text(entry, "etag"));
                    var observation = new OwnerAdmissionObservation(key, label, etag,
                        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
                    if (seen.TryGetValue((key, label), out var earlier))
                    {
                        if (earlier != observation) throw new BoundaryInspectionException("inconsistent owner admission pages");
                        continue;
                    }
                    seen.Add((key, label), observation);
                    observations.Add(observation);
                    var conflict = claims.Any(c => c.Key == key)
                        || string.Equals(label, selection.ProductKey, StringComparison.OrdinalIgnoreCase);
                    if (key == ProductConfigurationKeys.OwnerIndexGitHubRepository)
                    {
                        var name = value.Trim().Trim('"');
                        conflict |= name.Equals(selection.Repository, StringComparison.OrdinalIgnoreCase)
                            || name.Equals($"{repository?.Owner}/{repository?.Name}", StringComparison.OrdinalIgnoreCase);
                    }
                    if (key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix, StringComparison.Ordinal))
                    {
                        using var record = AzureBoundaryReader.Parse("owner admission record", () => JsonDocument.Parse(value));
                        if (record.RootElement.ValueKind != JsonValueKind.Object) throw new BoundaryInspectionException("owner admission record");
                        conflict |= record.RootElement.EnumerateObject().Any(p =>
                            p.Name.Equals("productKey", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String
                            && p.Value.GetString()?.Equals(selection.ProductKey, StringComparison.OrdinalIgnoreCase) == true
                            || p.Name.Equals("repositoryId", StringComparison.OrdinalIgnoreCase) && p.Value.ToString() == repository?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        if (!conflict)
                            findings.Add(AzureBoundaryReader.Unavailable("owner-admission",
                                "An existing onboarding record has no proven non-overlap with the requested product/repository/resources. "
                                + "Unknown record kinds and partial reservations are not unclaimed; reconcile trusted owner state."));
                    }
                    if (conflict)
                        findings.Add(new("owner-admission", BoundaryPlanState.Blocked,
                            "Existing owner registration/claim signals overlap this selection; matching names or values never authorize adoption.",
                            "owner registry administrator", "Reconcile trusted current registration/partial claims outside this preview; no reservation or adoption was performed."));
                }
            }
            foreach (var registration in observations.Where(o => o.Key is ProductConfigurationKeys.OwnerIndexGitHubRepository
                         or ProductConfigurationKeys.OwnerIndexAppConfigEndpoint).GroupBy(o => o.Label))
            {
                if (string.IsNullOrWhiteSpace(registration.Key) || registration.Count() != 2)
                    findings.Add(AzureBoundaryReader.Unavailable("owner-admission",
                        "Owner runtime index contains a partial or unlabelled registration; missing registration metadata is not unclaimed state."));
            }
        }
        catch (BoundaryInspectionException exception)
        {
            findings.Add(AzureBoundaryReader.Unavailable("owner-admission", exception.Message));
        }
        return new(selection.OwnerAppConfigEndpoint, observations, claims, findings);
    }
}
