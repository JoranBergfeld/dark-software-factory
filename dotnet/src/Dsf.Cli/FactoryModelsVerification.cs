using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryModelsService
{
    private static readonly HttpClient WorkloadHttp = CreateWorkloadHttp(new HttpClientHandler { AllowAutoRedirect = false });

    private static HttpClient CreateWorkloadHttp(HttpMessageHandler transport) => new(transport, disposeHandler: false)
    { Timeout = FactoryModelsValidation.ProofRequestBudget(FactoryModelsValidation.MaximumProbeSeconds) };

    internal async Task<FactoryReservationResult> CheckAsync(FactoryReservationResult admission, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return admission with { State = FactoryReservationState.Canceled, Detail = "Canceled before hold; no hold confirmed or revocation asserted." };
        if (admission.Models is not { Stage: "prepared" or "checking" or "observed" } selected)
            return admission with { State = FactoryReservationState.Blocked, Detail = "Complete provenance-proven model preparation required; unknown effects remain held without retry." };
        var store = Store(admission);
        var arm = new FactoryPreparationArm(azure, http, admission.Identity!);
        var held = false;
        try
        {
            root = await store.ReadAsync(ProductConfigurationKeys.FactoryModels, token) ?? throw new JsonException();
            var (_, manifest) = await ReadGovernanceAsync(admission, store, token);
            governance = FactoryReservationRecords.Serialize(manifest);
            current = FactoryModelsValidation.ReadPreparation(manifest, await ValuesAsync(store, selected, token));
            if (Serialize(current) != root.Value || current.ApprovalFingerprint != selected.ApprovalFingerprint) throw new JsonException();
            effects.Clear();
            effects.AddRange(current.Effects!);
            current = current with { State = ModelCapabilityState.Partial, Stage = "checking", CheckId = Guid.NewGuid(),
                VerifiedUntil = null, Proof = null, Detail = "Current model capability held pending real workload proof; previous proof is historical." };
            await SaveAsync(store, token);
            held = true;
            await EnsureCurrentAsync(admission, store, token);
            await CheckPreparedAsync(admission, arm, token);
            var credential = new OwnerReservationStore.OperatorCredential(azure, admission.Identity!.TenantId, admission.Identity.SubscriptionId,
                admission.Identity.AzureActorId, "api://" + current.Configuration.AudienceClientId);
            var access = await credential.GetTokenAsync(new TokenRequestContext(["api://" + current.Configuration.AudienceClientId + "/.default"]), token);
            using var request = new HttpRequestMessage(HttpMethod.Post, current.HostEndpoint + "/proof");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
            await EnsureCurrentAsync(admission, store, token);
            ModelCapabilityProof proof;
            using (var deadline = new CancellationTokenSource(FactoryModelsValidation.ProofRequestBudget(current.Configuration.ValidForSeconds), clock))
            using (var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                using var proofHttp = modelProofTransport is null ? null : CreateWorkloadHttp(modelProofTransport);
                using var response = await (proofHttp ?? http ?? WorkloadHttp).SendAsync(request, pending.Token);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    throw FactoryModelsValidation.Failure(ModelCapabilityReason.PermissionDenied, "host", "Native model host caller/governance denied; Entra/model administrator must reconcile.");
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict)) throw new JsonException();
                proof = JsonSerializer.Deserialize<ModelCapabilityProof>(await response.Content.ReadAsStringAsync(pending.Token), FactoryGitHubJson.Options)
                    ?? throw new JsonException();
                if (proof.State == ModelCapabilityState.Verified && response.StatusCode != HttpStatusCode.OK) throw new JsonException();
            }
            FactoryModelsValidation.ValidateProof(current, proof, clock.GetUtcNow(), requireCurrent: true);
            await CheckPreparedAsync(admission, arm, token);
            await EnsureCurrentAsync(admission, store, token);
            var proofKey = ProductConfigurationKeys.ModelProof(current.OperationId, current.CheckId!.Value);
            await store.WriteAsync(proofKey, FactoryReservationRecords.Serialize(proof), null, token);
            await EnsureCurrentAsync(admission, store, token);
            FactoryModelsValidation.ValidateProof(current, proof, clock.GetUtcNow(), requireCurrent: true);
            current = current with
            {
                State = proof.State, Stage = "observed", LastProofReference = proofKey, LastProofSha256 = FactoryModelsValidation.Hash(proof),
                VerifiedUntil = proof.State == ModelCapabilityState.Verified ? proof.ExpiresAt : null, Proof = proof,
                Detail = proof.State == ModelCapabilityState.Verified
                    ? "All required hosted model routes verified under the reviewed identity/configuration. Not council readiness or activation."
                    : "Model capability blocked: " + string.Join("; ", proof.Observations.Where(o => o.State != ModelCapabilityState.Verified).Select(o => o.Route + "=" + o.Reason)),
            };
            await SaveAsync(store, token);
            await EnsureCurrentAsync(admission, store, token);
            return Result(admission, current.State, current.Detail);
        }
        catch (ModelCapabilityException e)
        { return await CloseAsync(admission, store, ModelCapabilityState.Blocked, $"{e.Route}: {e.Reason}. {e.Message}", token); }
        catch (ReservationStoreException e)
        { return await CloseAsync(admission, store, Enum.Parse<ModelCapabilityState>(e.State.ToString()), e.Message, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Result(admission, ModelCapabilityState.Canceled, held
            ? "Canceled after a hold was confirmed; current final authority/hold unconfirmed. No revocation asserted; previous proof historical."
            : "Canceled before a hold was confirmed; no revocation asserted. Inspect current authority."); }
        catch (OperationCanceledException)
        { return await CloseAsync(admission, store, ModelCapabilityState.Unknown, held
            ? "Model verification transport timed out after a hold was confirmed; proof result unknown. Previous proof historical; no current revocation asserted or retry."
            : "Model verification transport timed out before a hold was confirmed; no revocation asserted. Inspect current authority.", token); }
        catch (HttpRequestException)
        { return await CloseAsync(admission, store, ModelCapabilityState.Blocked, "NetworkUnavailable: actual private hosted model path unavailable; no local probe or public-access fallback.", token); }
        catch (Exception e) when (ProviderFailure(e))
        { return await CloseAsync(admission, store, ModelCapabilityState.Unknown, "Current model proof/governance/receipt malformed or changed; no capability promotion. Inspect retained records, no retry.", token); }
    }

    private async Task CheckPreparedAsync(FactoryReservationResult admission, FactoryPreparationArm arm, CancellationToken token)
    {
        await CheckPrerequisitesAsync(admission, current!, arm, token);
        foreach (var effect in effects.Where(e => e.DeploymentTarget is not null))
        {
            using var native = await ReadNativeAsync(arm, effect.Target, effect.ApiVersion, token);
            var observed = ObserveHostResource(native.RootElement, effect.Target);
            if (observed.Incarnation != effect.Incarnation || observed.Revision != effect.NativeRevision)
                throw FactoryModelsValidation.Failure(ModelCapabilityReason.IdentityMismatch, "host", "Current exact workload host/auth policy differs from creation receipt.");
        }
        if (await CheckHostAsync(admission, arm, token) != current!.HostEndpoint) throw new JsonException();
    }
}
