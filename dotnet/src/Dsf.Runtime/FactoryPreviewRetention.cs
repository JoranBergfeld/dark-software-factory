using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    private static async Task<PreviewAnalysisRetentionReceipt> RetainPreviewAnalysisAsync(
        FactoryHostSettings settings, RuntimeDependencies dependencies, PreviewCapabilitySnapshot capability,
        PreviewAnalysisRetentionRequest request, HttpClient http, CancellationToken token)
    {
        var credential = dependencies.SourceCredential
            ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
        var source = new FactorySourceAccess(settings, credential, http, dependencies.SourceClock);
        await source.CheckAsync(token);
        var records = new AzureCharterEvidenceReader(settings.ProductEndpoint, source.Credential, http);
        var authority = new AzureFactoryPreviewCapability(settings, PreviewHostKind.SourceContext,
            credential, http, dependencies.SourceClock);
        var context = await RuntimeWorkflow.ReadSourceCharterContextAsync(source, records, http, dependencies.SourceClock, token,
            async cancellation => (await authority.CheckAsync(request.Preview, cancellation)).GovernanceRevision);
        ValidatePreviewContext(capability, context);
        var analysis = context.Current!.Analysis.Progress!;
        if (analysis.Revision != request.Revision || analysis.ChainSha256 != request.ChainSha256
            || analysis.Snapshot.Id != request.SourceSnapshotId || analysis.ModelConfigurationRevision != request.ModelRevision
            || context.Adoption!.Adoption.Revision != request.AdoptionRevision)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CapturedAnalysisChangedBeforeRetention");
        var count = analysis.AnalyzedChunks + analysis.CheckedChunks;
        var store = new SourceProgressStore(source, http, "intent");
        var key = FactoryAuditRetention.EvidenceKey(request.Revision);
        var retained = await store.ReadAsync(key, token);
        if (retained is null)
            retained = await store.WriteAsync(key, new AnalysisEvidenceClaim(1, source.Manifest.Identity.FactoryId,
                request.Revision, request.ChainSha256, AnalysisRetentionKind.Protected, request.Preview.OperationId,
                FactoryModelsValidation.Hash(request), count), null, token);
        var hold = SourceProgressStore.Parse<AnalysisEvidenceClaim>(retained);
        FactoryAuditRetention.RequireEvidence(hold, source.Manifest.Identity.FactoryId, request.Revision, request.ChainSha256, count);
        if (hold.Kind != AnalysisRetentionKind.Protected)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "AnalysisExpiryAlreadyAdmitted");
        var current = await store.ReadAsync("intent/current", token);
        if (current is null || SourceProgressStore.Parse<IntentProgress>(current) is not { } currentProgress
            || currentProgress.Revision != request.Revision || currentProgress.ChainSha256 != request.ChainSha256)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "AnalysisChangedDuringRetention");
        await source.CheckAsync(token);
        return new(request.Preview.RequestId, hold.FactoryId, hold.Revision, hold.ChainSha256, retained.ETag.ToString());
    }

    internal static async Task RetainPreviewSourceAsync(PreviewCapabilitySnapshot capability, PreviewInputSnapshot inputs,
        TokenCredential credential, HttpClient http, TimeProvider clock, CancellationToken token)
    {
        var progress = inputs.Source.Context.Current!.Analysis.Progress!;
        var input = new PreviewAnalysisRetentionRequest(inputs.Request, progress.Revision, progress.ChainSha256!,
            progress.Snapshot.Id, progress.ModelConfigurationRevision, inputs.Source.Context.Adoption!.Adoption.Revision);
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, capability.SourceContextEndpoint + "/retain-council-context")
        { Content = JsonContent.Create(input, options: FactoryGitHubJson.Options) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await credential.GetTokenAsync(
            new TokenRequestContext(["api://" + capability.Capability.Configuration.AudienceClientId + "/.default"]), pending.Token)).Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pending.Token);
        using var body = await EvidencePayload.ReadAsync(response, pending.Token, 4096);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var failure = body.RootElement.Deserialize<PreviewContextFailure>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (!Unconfirmed(failure.State)) throw new JsonException();
            throw new PreviewCapabilityException(failure.State, "AnalysisRetentionUnconfirmed");
        }
        var receipt = body.RootElement.Deserialize<PreviewAnalysisRetentionReceipt>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (receipt.RequestId != input.Preview.RequestId || receipt.FactoryId != capability.Manifest.Identity.FactoryId
            || receipt.Revision != input.Revision || receipt.ChainSha256 != input.ChainSha256 || string.IsNullOrWhiteSpace(receipt.NativeRevision))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "AnalysisRetentionReceiptChanged");
    }
}
