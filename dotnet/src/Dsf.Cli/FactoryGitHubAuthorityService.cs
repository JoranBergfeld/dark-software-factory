using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryGitHubAuthorityService
{
    internal const string Risk = FactoryGitHubAuthorityValidation.SigningRisk;

    internal static FactoryGitHubAuthority Preview(FactoryReservationResult admission,
        string? sourceVaultId, string? version, string? image, string? audience)
    {
        if (admission.Foundation?.State != FactoryFoundationState.Inactive || admission.Identity is null || admission.Plan is null)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "GitHub authority requires completed, provenance-proven inactive foundation.");
        var identity = admission.Identity;
        var source = sourceVaultId?.ToLowerInvariant() ?? "";
        if (!Regex.IsMatch(source, "^/subscriptions/[0-9a-f-]{36}/resourcegroups/[a-z0-9_.()-]+/providers/microsoft.keyvault/vaults/[a-z0-9-]{3,24}$")
            || new Uri(admission.Plan.Selection.OwnerKeyVaultUri).Host != source.Split('/').Last() + ".vault.azure.net"
            || version is null || !Regex.IsMatch(version, "^[0-9a-f]{32}$")
            || image is null || !Regex.IsMatch(image, "^[a-z0-9.-]+/[a-zA-Z0-9_./-]+@sha256:[0-9a-f]{64}$")
            || !Guid.TryParse(audience, out var client) || client == Guid.Empty)
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Exact --source-vault-id matching the reviewed owner vault, --source-key-version (32 hex), "
                + "--runtime-image (implemented DSF runtime digest), and --auth-audience (existing factory API application client ID) required. "
                + "Owner vault / image / Entra administrators supply missing prerequisites; no new registrations or permissions are created.");
        var operations = admission.Foundation.Operations;
        var auth = Proven(o => o.LogicalIdentity == "factory:github-auth");
        var code = Proven(o => o.LogicalIdentity == "factory:code-intent");
        var vault = Proven(o => o.Target.Contains("/providers/microsoft.keyvault/vaults/", StringComparison.Ordinal));
        var assets = new FactoryFoundationAssets(identity, admission.Plan.Selection);
        var hosting = Proven(o => o.Target == assets.HostingEnvironment);
        var otherIdentities = new[] { Proven(o => o.LogicalIdentity == "factory:council"),
            Proven(o => o.LogicalIdentity == "factory:source-azuremonitor") };
        if (!Guid.TryParse(auth.PrincipalId, out var authPrincipal) || authPrincipal == Guid.Empty
            || !Guid.TryParse(auth.ClientId, out var authClient) || authClient == Guid.Empty
            || !Guid.TryParse(code.PrincipalId, out var codePrincipal) || codePrincipal == Guid.Empty
            || !Guid.TryParse(code.ClientId, out var codeClient) || codeClient == Guid.Empty
            || authPrincipal == codePrincipal || authClient == codeClient)
            throw new JsonException();
        var app = admission.Plan.RepositoryReview.App ?? throw new JsonException();
        var installation = admission.Plan.RepositoryReview.Installation ?? throw new JsonException();
        if (installation.AppId != app.Id || installation.AccountId != admission.Plan.RepositoryReview.Repository?.OwnerId)
            throw new JsonException();
        var draft = new FactoryGitHubAuthority(1, identity.FactoryId, Guid.Empty, identity.PlanFingerprint,
            GitHubAuthorityState.Partial, "preview", app.Id, installation.Id, installation.AccountId, identity.RepositoryId,
            installation.GrantedPermissions, app.RequestedPermissions, installation.RepositorySelection,
            source, version, vault.Target, null, auth.Target, auth.PrincipalId!, auth.ClientId!,
            code.PrincipalId!, code.ClientId!, false)
        {
            RuntimeImage = image, AudienceClientId = client.ToString(), Detail = Risk,
            KeyCopyTemplateSha256 = FactoryReservationRecords.Hash(File.ReadAllBytes(new ProvisioningAssets().Resolve("product-github-private-key.json"))),
            HostTemplateSha256 = FactoryReservationRecords.Hash(File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-github-auth.json"))),
            HostResourceId = assets.Group + "/providers/microsoft.app/containerapps/" + assets.Prefix + "-github-auth",
            HostingEnvironmentId = hosting.Target,
            BindingIncarnations = new[] { auth, code, vault, hosting }.Concat(otherIdentities).ToDictionary(o => o.Target, o => o.Incarnation!),
            OtherWorkloadPrincipalIds = otherIdentities.Select(o => o.PrincipalId ?? throw new JsonException()).ToArray(),
            FactoryResourceGroupId = assets.Group, FoundationOperationId = admission.Foundation.OperationId,
            BindingReceiptHashes = new Dictionary<string, FoundationOperation>
            {
                ["auth"] = auth, ["code"] = code, ["council"] = otherIdentities[0], ["source"] = otherIdentities[1],
                ["vault"] = vault, ["hosting"] = hosting,
            }.ToDictionary(p => p.Key, p => FactoryGitHubAuthorityValidation.ReceiptHash(p.Value)),
        };
        return draft with { ApprovalFingerprint = FactoryGitHubAuthorityValidation.ApprovalFingerprint(draft) };

        FoundationOperation Proven(Func<FoundationOperation, bool> predicate)
        {
            var found = operations.Where(predicate).ToArray();
            if (found.Length != 1 || found[0].State != FoundationOperationState.Completed
                || found[0].Disposition != FoundationResourceDisposition.Created || found[0].ResourceId != found[0].Target
                || string.IsNullOrEmpty(found[0].Incarnation) || string.IsNullOrEmpty(found[0].ProviderOperationId))
                throw new ReservationStoreException(FactoryReservationState.Blocked, "A required binding lacks completed creation provenance; names, Reused and Unproven are not proof.");
            return found[0];
        }
    }
}
