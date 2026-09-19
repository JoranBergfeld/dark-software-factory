using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

public sealed class PreviewInputAcquisition(PreviewInputSnapshot snapshot, IFactoryPreviewModelWorkloads models)
{
    public PreviewInputSnapshot Snapshot { get; } = snapshot;
    [JsonIgnore] public IFactoryPreviewModelWorkloads Models { get; } = models;
}

public static partial class RuntimeVerbs
{
    public static Task<PreviewInputAcquisition> AcquirePreviewInputsAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        PreviewCapabilityRequest request, CancellationToken token) =>
        RuntimeWorkflow.AcquirePreviewInputsAsync(settings, dependencies, request, dependencies.PreviewHttp ?? PreviewHttp, token);
}

internal static partial class RuntimeWorkflow
{
    internal static async Task<PreviewInputAcquisition> AcquirePreviewInputsAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        PreviewCapabilityRequest request, HttpClient http, CancellationToken cancellationToken)
    {
        var clock = dependencies.PreviewClock;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(FactoryPreviewInputValidation.Policy.RequestSeconds), clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = pending.Token;
        try
        {
            if (http.Timeout != Timeout.InfiniteTimeSpan && http.Timeout < FactoryPreviewValidation.RequestBudget)
                throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewHttpTimeoutMustCoverConsumerBudget");
            var credential = dependencies.PreviewCredential ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
            var validator = new AzureFactoryPreviewCapability(settings, PreviewHostKind.Council, credential, http, clock);
            var authority = await validator.CheckAsync(request, token);
            var config = authority.Capability.Configuration;
            var inputs = config.Inputs ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewInputsRequired:configuration.inputs");
            FactoryPreviewInputValidation.Validate(inputs);
            if (config.ModelEvaluation is null || authority.Models is null)
                throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewModelConsumerRequired");
            var selection = config.Telemetry ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewTelemetryProjectionRequired:configuration.telemetry");
            var telemetryConfiguration = authority.Telemetry?.Configuration ?? throw new JsonException();
            foreach (var identifiers in new[] { telemetryConfiguration.ApplicationResourceIds, telemetryConfiguration.Backends.Select(b => b.ResourceId).ToArray() })
                if (identifiers.Count is < 1 or > 32 || identifiers.Any(id => id.Length > 512))
                    throw new PreviewCapabilityException(FactoryCharterState.Partial, "ApplicationScopeExceedsEvaluationBudget");
            var workload = new FactoryWorkloadCredential(credential, authority.Manifest.Identity.TenantId, authority.Council, clock);
            var source = await ReadSourceAsync();
            var models = await OpenPreviewModelsAsync(settings, dependencies, request, http, token);
            var supplemental = await ReadPreviewSupplementalAsync(settings, dependencies, request, http, token);
            RequireSupplemental(supplemental);
            var telemetry = await ReadPreviewTelemetryAsync(settings, dependencies, request, http, token);
            RequireTelemetry(telemetry);

            // Re-observe current authority without replacing captured content or comparing ordinary new rows.
            var currentSupplemental = await ReadPreviewSupplementalAsync(settings, dependencies, request, http, token);
            RequireSupplemental(currentSupplemental);
            var currentTelemetry = await ReadPreviewTelemetryAsync(settings, dependencies, request, http, token);
            RequireTelemetry(currentTelemetry);
            if (telemetry.SourceAuthorityRevision != currentTelemetry.SourceAuthorityRevision
                || !SupplementalRevisions(supplemental).OrderBy(p => p.Key).SequenceEqual(SupplementalRevisions(currentSupplemental).OrderBy(p => p.Key)))
                throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewSourceAuthorityChangedDuringCollection");
            await models.ReadAuthorityAsync(token);
            if (models.Observations.Any(o => o.CapabilityRevision != authority.Revision || o.Output is not null)
                || ModelRevision(models.Observations[0].Authority) != ModelRevision(models.Observations[^1].Authority))
                throw new JsonException("Exact evaluation model authority required.");
            var currentSource = await ReadSourceAsync();
            if (SourceRevision(currentSource) != SourceRevision(source))
                throw new PreviewCapabilityException(FactoryCharterState.Blocked, "CurrentAdoptionOrAnalyzedSourceChangedDuringCollection");
            var current = await validator.CheckAsync(request, token);
            if (current.Revision != authority.Revision)
                throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedDuringInputRead");
            FactoryPreviewTelemetryValidation.ValidateCapture(authority.Telemetry!, selection, request.RequestId, telemetry.Capture, clock.GetUtcNow());
            foreach (var item in supplemental.Sources) FactoryPreviewSupplementalValidation.ValidateEvidence(item.Authority, item.Evidence, clock.GetUtcNow());
            token.ThrowIfCancellationRequested();

            var observations = new List<PreviewInputEvidence>();
            var provenance = new List<CouncilEvidenceProvenance>();
            foreach (var row in telemetry.Capture.Rows)
            {
                var reference = "azuremonitor:sha256:" + FactoryModelsValidation.Hash(new { row.Reference.BackendResourceId,
                    row.Reference.WorkspaceId, row.Reference.ApplicationResourceId, row.Reference.Table, row.Reference.ItemId, row.Reference.RecordTime });
                var fields = FactoryPreviewTelemetryValidation.Fields(row.Reference.Table).ToDictionary(f => f.Name, f => row.Fields[f.Name]);
                Add("azuremonitor", reference, JsonSerializer.Serialize(fields, FactoryGitHubJson.Options), selection.ConfigurationRevision,
                    "telemetry/capture/rows/" + observations.Count, row.Reference.RecordTime);
            }
            for (var s = 0; s < supplemental.Sources.Count; s++)
            {
                var selected = supplemental.Sources[s];
                for (var i = 0; i < selected.Evidence.Items.Count; i++)
                {
                    var item = selected.Evidence.Items[i];
                    Add(selected.Evidence.Proof.Kind, item.Reference, item.Content, selected.Evidence.Proof.ConfigurationRevision,
                        $"supplemental/sources/{s}/evidence/items/{i}", selected.Evidence.Proof.ObservedAt);
                }
            }
            var context = new CouncilEvaluationContext(authority.Manifest.Identity.FactoryId, FactoryModelsValidation.Hash(authority.Manifest),
                source.Context.Adoption!.Adoption, source.Context.Current!,
                new(telemetryConfiguration.ApplicationEnvironment, telemetryConfiguration.ApplicationResourceIds, telemetryConfiguration.Backends.Select(b => b.ResourceId).ToArray()),
                provenance, new(authority.Models.ApprovalFingerprint, authority.Models.Routes!.Select(r => r.Selection).ToArray()), inputs.Judgment);
            var snapshot = new PreviewInputSnapshot(1, PreviewInputState.Captured, request, clock.GetUtcNow(),
                authority.Manifest, authority.Capability, authority.Revision, authority.GovernanceRevision, context, observations,
                source, selection, telemetryConfiguration, telemetry, supplemental, config.Supplemental, models.Observations,
                new(SourceRevision(currentSource), currentTelemetry.SourceAuthorityRevision, SupplementalRevisions(currentSupplemental),
                    ModelRevision(models.Observations[^1].Authority))) { ExecutionConfiguration = config.Execution };
            snapshot = snapshot with { Revision = snapshot.CalculateRevision() };
            if (JsonSerializer.SerializeToUtf8Bytes(snapshot, FactoryGitHubJson.Options).Length > inputs.Policy.SnapshotBytes)
                throw new PreviewCapabilityException(FactoryCharterState.Partial, "CompletePreviewInputsExceedSnapshotBudget");
            return new(snapshot, models);

            void Add(string kind, string reference, string content, string revision, string location, DateTimeOffset observed)
            {
                if (observations.Count >= inputs.Policy.MaximumObservations || content.Length > inputs.Policy.ObservationCharacters)
                    throw new PreviewCapabilityException(FactoryCharterState.Partial, "CompletePreviewEvidenceExceedsEvaluationBudget");
                if (observations.Any(o => o.Reference == reference)) throw new JsonException("Duplicate cross-source reference.");
                observations.Add(new(kind, reference, content));
                provenance.Add(new(kind, reference, FactoryModelsValidation.Hash(content), revision, "input:" + request.RequestId.ToString("D") + "#/" + location, observed));
            }
            async Task<PreviewSourceContext> ReadSourceAsync()
            {
                using var sourceDeadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, clock);
                using var sourcePending = CancellationTokenSource.CreateLinkedTokenSource(token, sourceDeadline.Token);
                var result = await RuntimeVerbs.ReadPreviewSourceAsync(authority, request, workload, http, sourcePending.Token);
                sourcePending.Token.ThrowIfCancellationRequested();
                if (result.Context.State != FactoryCharterState.Adopted)
                    throw new PreviewCapabilityException(result.Context.State, "CurrentAdoptedSourceContextRequired");
                RuntimeVerbs.ValidatePreviewContext(authority, result.Context);
                var analysis = result.Context.Current!.Analysis;
                if (analysis.Progress!.ModelConfigurationRevision != authority.Models.ApprovalFingerprint
                    || result.Context.Adoption!.Adoption.ModelRevision != authority.Models.ApprovalFingerprint)
                    throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewAnalysisModelConfigurationChanged");
                FactoryModelsValidation.ValidateNativeAuthority(authority.Models, analysis.ModelAuthority!.CheckId, analysis.ModelAuthority);
                return result;
            }
            void RequireTelemetry(PreviewTelemetryEvidence evidence)
            {
                if (evidence.GovernanceRevision != authority.GovernanceRevision || telemetryConfiguration.ApplicationEnvironment != authority.Manifest.Identity.ApplicationEnvironment)
                    throw new JsonException("Inconsistent telemetry authority.");
                if (evidence.Capture.Proof.State != TelemetryState.Ready)
                    throw new PreviewCapabilityException(evidence.Capture.Proof.State switch {
                        TelemetryState.Unknown => FactoryCharterState.Unknown, TelemetryState.Canceled => FactoryCharterState.Canceled,
                        TelemetryState.Failed => FactoryCharterState.Failed, TelemetryState.Partial => FactoryCharterState.Partial, _ => FactoryCharterState.Blocked },
                        "RequiredApplicationTelemetryUnavailable:" + evidence.Capture.Proof.Reason);
            }
            void RequireSupplemental(PreviewSupplementalEvidence evidence)
            {
                if (evidence.GovernanceRevision != authority.GovernanceRevision) throw new JsonException("Inconsistent selected-source authority.");
                if (evidence.State is not (SupplementalState.ReadySupplemental or SupplementalState.NotSelected))
                    throw new PreviewCapabilityException(evidence.State switch {
                        SupplementalState.Unknown => FactoryCharterState.Unknown, SupplementalState.Canceled => FactoryCharterState.Canceled,
                        SupplementalState.Partial => FactoryCharterState.Partial, SupplementalState.Failed => FactoryCharterState.Failed, _ => FactoryCharterState.Blocked },
                        "SelectedSupplementalUnavailable");
            }
        }
        catch (PreviewCapabilityException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewInputCollectionDeadlineExceeded"); }
        catch (PreviewCapabilityException) { throw; }
        catch (OperationCanceledException)
        { throw new PreviewCapabilityException(cancellationToken.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Unknown, "PreviewInputCollectionCanceledOrUnconfirmed"); }
        catch (SupplementalSourceException e) when (e.Reason == SupplementalReason.CapacityExceeded)
        { throw new PreviewCapabilityException(FactoryCharterState.Partial, "CompletePreviewInputsExceedTransportBudget"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException
            or HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException or ModelCapabilityException)
        { throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewInputCollectionUnconfirmed"); }
    }

    private static string ModelRevision(ModelAuthorityResult authority) =>
        FactoryModelsValidation.Hash(authority with { CheckId = Guid.Empty, ContinuationProtocol = null });
    private static string SourceRevision(PreviewSourceContext source)
    {
        var current = source.Context.Current!;
        return FactoryModelsValidation.Hash(source.Context with { Current = current with {
            // Current reads have new observation times/nonces, not new repository or model revisions.
            Source = current.Source with { Snapshot = current.Source.Snapshot with { ObservedAt = DateTimeOffset.UnixEpoch } },
            Analysis = current.Analysis with { ModelAuthority = current.Analysis.ModelAuthority! with { CheckId = Guid.Empty, ContinuationProtocol = null } } } });
    }
    private static IReadOnlyDictionary<string, string> SupplementalRevisions(PreviewSupplementalEvidence evidence) =>
        evidence.Sources.ToDictionary(s => s.Evidence.Proof.Kind, s => FactoryModelsValidation.Hash(new {
            s.Authority, s.NativeRevisions, s.Evidence.Proof.NativeSourceRevision }));
}
