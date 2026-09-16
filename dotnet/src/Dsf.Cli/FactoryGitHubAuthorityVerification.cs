using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryGitHubAuthorityService
{
    internal async Task<FactoryReservationResult> ReverifyAsync(FactoryReservationResult admission, CancellationToken token)
    {
        if (admission.GitHubAuthority is not { Stage: "host-verification" or "verified", HostEndpoint: not null, AcceptedSigningRisk: true })
            return admission with { State = admission.State == FactoryReservationState.Reserved ? FactoryReservationState.Blocked : admission.State,
                Detail = "Verification-only requires the same completed, proven key/grant/host effects. Earlier uncertain effects remain held; no dispatch." };
        var store = ProductStore(admission);
        try
        {
            ValidateCompletedEffects(admission, admission.GitHubAuthority, admission.GitHubOperations);
            root = await store.ReadAsync(ProductConfigurationKeys.FactoryGitHubAuthority, token) ?? throw new JsonException();
            if (root.Value != FactoryReservationRecords.Serialize(admission.GitHubAuthority)) throw new JsonException();
            var manifest = FactoryReservationRecords.Parse<FactoryManifest>(
                (await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token))?.Value ?? throw new JsonException());
            if (manifest.GitHubAuthorityOperationId != admission.GitHubAuthority.OperationId) throw new JsonException();
            authority = admission.GitHubAuthority;
            manifestIntentConfirmed = true;
            operations.AddRange(admission.GitHubOperations);
            await VerifyRepositoryAutomationAsync(admission, token);
            await VerifyBaseBindingsAsync(admission, authority, token);
            await VerifyKeyIsolationAsync(admission.Identity!, authority, token);
            await VerifyNativeHostPolicyAsync(admission.Identity!, authority.HostResourceId!, token);
            using var host = await ArmAsync(HttpMethod.Get, authority.HostResourceId!, "2025-01-01", null, admission.Identity!, null, token);
            if (host.StatusCode != HttpStatusCode.OK) throw new JsonException();
            using var json = JsonDocument.Parse(await host.Content.ReadAsStringAsync(token));
            var current = json.RootElement;
            if (!string.Equals(current.GetProperty("id").GetString(), authority.HostResourceId, StringComparison.OrdinalIgnoreCase)
                || current.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset().ToString("O") != operations[3].Incarnation
                || current.GetProperty("systemData").GetProperty("lastModifiedAt").GetDateTimeOffset().ToString("O") != operations[3].Incarnation
                || "https://" + current.GetProperty("properties").GetProperty("configuration").GetProperty("ingress").GetProperty("fqdn").GetString() != authority.HostEndpoint)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "GET deployed authentication host incarnation/endpoint changed; factory administrator must reconcile. No adoption or replacement.");
            await VerifyDeployedAsync(store, admission.Identity!, token);
            return CapabilityResult(admission);
        }
        catch (ReservationStoreException e)
        { return await CloseAsync(admission, store, Enum.Parse<GitHubAuthorityState>(e.State.ToString()), e.Message, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Result(admission, GitHubAuthorityState.Canceled, "Verification canceled; historical proof remains inspectable, no creation effects reissued."); }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException
            or HttpRequestException or OperationCanceledException or OwnerReservationStore.AuthenticationFailedException)
        { return await CloseAsync(admission, store, GitHubAuthorityState.Unknown, "Deployed verification evidence unavailable or invalid; factory operator must inspect held authority.", token); }
    }

    private async Task VerifyRepositoryAutomationAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var selection = admission.Plan!.Selection;
        var repository = admission.Plan.RepositoryReview.Repository!;
        var current = await (reviewer ?? RepositoryAttachmentReviewer.Create()).ReviewAsync(
            new RepositoryAttachmentReviewRequest(repository.Owner + "/" + repository.Name,
                selection.OwnerAppConfigEndpoint, selection.OwnerKeyVaultUri)
            { AutomationDeclarations = selection.AutomationDeclarations, AutomationOwner = selection.AutomationOwner }, token);
        var publication = current.Automation.SingleOrDefault(r => r.Mutation == RepositoryMutationClass.ProposalIssue);
        if (current.Repository?.Id != admission.RepositoryId || publication?.State != RepositoryReviewState.Confirmed)
            throw new ReservationStoreException(publication?.State == RepositoryReviewState.Unknown
                    ? FactoryReservationState.Unknown : FactoryReservationState.Blocked,
                "GET current repository automation inventory for ProposalIssue is not clear. Responsible administrator: "
                + selection.AutomationOwner + ". Existing triggers/settings stay unchanged; no key copy or grants.");
    }

    private async Task VerifyDeployedAsync(OwnerReservationStore store, FactoryReservationIdentity identity, CancellationToken token)
    {
        var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
            identity.AzureActorId, "api://" + authority!.AudienceClientId);
        AccessToken access;
        try { access = await credential.GetTokenAsync(new TokenRequestContext(["api://" + authority.AudienceClientId + "/.default"]), token); }
        catch (OwnerReservationStore.AuthenticationFailedException)
        {
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Acquire Entra bearer for api://" + authority.AudienceClientId + " before POST " + authority.HostEndpoint
                + "/verify denied or unverifiable; factory Entra API administrator must resolve existing operator consent/identity. No new registration or consent.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, authority.HostEndpoint + "/verify");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        HttpResponseMessage response;
        try { response = await (http ?? ProviderHttp).SendAsync(request, token); }
        catch (HttpRequestException)
        {
            throw new ReservationStoreException(FactoryReservationState.Unknown,
                "POST " + authority.HostEndpoint + "/verify unreachable; factory network administrator must establish the private deployed path. No operator GitHub fallback.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden && response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "POST deployed /verify denied; Entra API / factory network administrator must establish operator consent and the private deployed path. No alternate operator GitHub proof.");
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Forbidden) throw new JsonException();
            var proof = JsonSerializer.Deserialize<GitHubAuthorityProof>(await response.Content.ReadAsStringAsync(token),
                FactoryGitHubJson.Options) ?? throw new JsonException();
            if (response.StatusCode == HttpStatusCode.Forbidden && proof.State == GitHubAuthorityState.Blocked
                && proof.Operation == "product-governance" && proof.FactoryId == Guid.Empty && proof.OperationId == Guid.Empty)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "POST " + authority.HostEndpoint + "/verify: product-governance blocked. Responsible administrator: "
                    + proof.ResponsibleAdmin + ". " + proof.Detail);
            ValidateProof(authority, proof);
            if (!response.IsSuccessStatusCode && proof.State == GitHubAuthorityState.Verified) throw new JsonException();
            authority = authority with { State = proof.State, Proof = proof,
                Stage = proof.State == GitHubAuthorityState.Verified ? "verified" : "host-verification", Detail = proof.Detail + " " + Risk };
            await SaveAsync(store, token);
        }
    }

    private FactoryReservationResult CapabilityResult(FactoryReservationResult admission) =>
        Result(admission, authority!.State, authority.Detail);

    private static void ValidateCompletedEffects(FactoryReservationResult admission, FactoryGitHubAuthority authority,
        IReadOnlyList<FoundationOperation> operations)
    {
        if (!authority.AcceptedSigningRisk || authority.ApprovalFingerprint is not { Length: 64 }
            || authority.ApprovalFingerprint.Any(c => !char.IsAsciiHexDigit(c))
            || operations.Count != 5 || operations.Any(o => o.State != FoundationOperationState.Completed
            || o.Disposition != FoundationResourceDisposition.Created || string.IsNullOrWhiteSpace(o.Incarnation)
            || o.ClientRequestId == Guid.Empty || o.StartedAt == default || o.RequestSha256.Length != 64))
            throw new JsonException();
        foreach (var index in new[] { 0, 3, 4 })
            if (string.IsNullOrWhiteSpace(operations[index].ProviderOperationId)
                || !Guid.TryParse(operations[index].ProviderRequestId, out var request) || request == Guid.Empty
                || !Guid.TryParse(operations[index].ProviderCorrelationId, out var correlation) || correlation == Guid.Empty)
                throw new JsonException();
        var assets = new FactoryFoundationAssets(admission.Identity!, admission.Plan!.Selection);
        var deployment = assets.Group + "/providers/microsoft.resources/deployments/gh-" + authority.OperationId.ToString("N");
        var roles = "/subscriptions/" + admission.Identity!.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/";
        if (operations[0].Target != deployment + "-key" || operations[0].Endpoint != authority.DestinationSecretUri
            || operations[0].ResourceId != authority.DestinationVaultId + "/secrets/github-app-private-key"
            || operations[1].RoleScope != operations[0].ResourceId
            || operations[2].RoleScope != assets.Group + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + authority.FactoryId.ToString("N")
            || operations[1].PrincipalId != authority.AuthPrincipalId || operations[2].PrincipalId != authority.AuthPrincipalId
            || operations[1].RoleDefinitionId != roles + "4633458b-17de-408a-b874-0445c86b69e6"
            || operations[2].RoleDefinitionId != roles + "516239f1-63e1-4d78-a4de-a74fb236a071"
            || operations[1].Condition is not null || operations[2].Condition is not null
            || operations[3].Target != deployment + "-host" || operations[3].ResourceId != authority.HostResourceId
            || operations[4].Target != authority.HostResourceId + "/authconfigs/current"
            || operations[4].DeploymentTarget != operations[3].Target || operations[4].ClientRequestId != operations[3].ClientRequestId)
            throw new JsonException();
    }
}
