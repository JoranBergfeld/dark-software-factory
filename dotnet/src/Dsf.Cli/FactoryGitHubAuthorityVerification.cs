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
            root = await store.ReadAsync(ProductConfigurationKeys.FactoryGitHubAuthority, token) ?? throw new JsonException();
            if (root.Value != FactoryReservationRecords.Serialize(admission.GitHubAuthority)) throw new JsonException();
            var manifest = FactoryReservationRecords.Parse<FactoryManifest>(
                (await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token))?.Value ?? throw new JsonException());
            if (manifest.GitHubAuthorityOperationId != admission.GitHubAuthority.OperationId) throw new JsonException();
            var values = await ReadGitHubRecordsAsync(store, admission.GitHubAuthority, token);
            authority = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
            var completed = FactoryGitHubAuthorityValidation.ReadCompletedEffects(authority, values);
            manifestIntentConfirmed = true;
            operations.AddRange(completed);
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
            await VerifyDeployedAsync(store, admission, token);
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

    private async Task VerifyRepositoryAutomationAsync(FactoryReservationResult admission, CancellationToken token, GitHubAuthorityProof? proof = null)
    {
        var selection = admission.Plan!.Selection;
        var repository = admission.Plan.RepositoryReview.Repository!;
        var current = await (reviewer ?? RepositoryAttachmentReviewer.Create()).ReviewAsync(
            new RepositoryAttachmentReviewRequest(proof?.Repository ?? repository.Owner + "/" + repository.Name,
                selection.OwnerAppConfigEndpoint, selection.OwnerKeyVaultUri)
            { DurableRepositoryId = admission.RepositoryId, AutomationDeclarations = selection.AutomationDeclarations,
                AutomationOwner = selection.AutomationOwner }, token);
        if (current.Repository is not { } observed || observed.Id != admission.RepositoryId)
            throw new ReservationStoreException(current.State == RepositoryReviewState.Unknown ? FactoryReservationState.Unknown : FactoryReservationState.Blocked,
                $"GET /repositories/{admission.RepositoryId} current metadata unavailable or identity differs. Responsible administrator: repository administrator / operator. No retargeting or access repair.");
        if (proof is not null && (observed.Owner + "/" + observed.Name != proof.Repository || observed.DefaultBranch != proof.DefaultBranch)
            || current.Findings.SingleOrDefault(f => f.Check == "repository-stability")?.State != RepositoryReviewState.Confirmed)
            throw new ReservationStoreException(FactoryReservationState.Unknown,
                $"GET /repositories/{admission.RepositoryId} metadata changed or is unverifiable across deployed proof and automation review. Responsible administrator: repository administrator / operator. Repeat read-only verification; no settings changes.");
        var invalidLabel = current.Labels.FirstOrDefault(label => label.SemanticState != RepositoryReviewState.Confirmed);
        if (invalidLabel is not null)
            throw new ReservationStoreException(invalidLabel.SemanticState == RepositoryReviewState.Unknown ? FactoryReservationState.Unknown : FactoryReservationState.Blocked,
                $"GET /repos/{observed.Owner}/{observed.Name}/labels/{Uri.EscapeDataString(invalidLabel.Name)} meaning/access is not clear. Responsible administrator: repository label / automation owner. No label changes.");
        var publication = current.Automation.SingleOrDefault(r => r.Mutation == RepositoryMutationClass.ProposalIssue);
        if (observed.Archived || observed.Disabled || !observed.HasIssues || publication?.State != RepositoryReviewState.Confirmed)
            throw new ReservationStoreException(publication?.State == RepositoryReviewState.Unknown
                    ? FactoryReservationState.Unknown : FactoryReservationState.Blocked,
                "GET current repository automation inventory for ProposalIssue is not clear. Responsible administrator: "
                + selection.AutomationOwner + ". Existing triggers/settings stay unchanged; any previously dispatched preparation effects remain inspectable.");
    }

    private async Task VerifyDeployedAsync(OwnerReservationStore store, FactoryReservationResult admission, CancellationToken token)
    {
        var identity = admission.Identity!;
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
            if (proof.State == GitHubAuthorityState.Verified)
            {
                await VerifyRepositoryAutomationAsync(admission, token, proof);
                var values = await ReadGitHubRecordsAsync(store, authority, token);
                var manifest = FactoryReservationRecords.Parse<FactoryManifest>(values[ProductConfigurationKeys.FactoryManifest]);
                var current = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
                if (FactoryReservationRecords.Serialize(current) != FactoryReservationRecords.Serialize(authority)) throw new JsonException();
                FactoryGitHubAuthorityValidation.ReadCompletedEffects(current, values);
            }
            authority = authority with { State = proof.State, Proof = proof,
                Stage = proof.State == GitHubAuthorityState.Verified ? "verified" : "host-verification", Detail = proof.Detail + " " + Risk };
            await SaveAsync(store, token);
        }
    }

    private FactoryReservationResult CapabilityResult(FactoryReservationResult admission) =>
        Result(admission, authority!.State, authority.Detail);

}
