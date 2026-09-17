using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Onboarding;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

public enum PreviewExecutionPhase { Prepared, Executing, Evaluated, Failed }
public sealed record PreviewRunReceipt(string Id, string ResourceId, string ETag, string Checksum);
internal sealed record PreviewCheckpointIntent(string Checksum, string Station);
internal sealed record PreviewEvaluationDocument(
    int Protocol, string Id, string Product, Guid FactoryId, Guid OperationId, DateTimeOffset CreatedAt)
{
    [JsonRequired] public string Kind { get; init; } = "council-preview-evaluation";
    [JsonRequired] public ConveyorProfile Profile { get; init; } = ConveyorProfile.OnboardingPreview;
    [JsonRequired] public string Checksum { get; init; } = "";
    [JsonRequired] public Guid Owner { get; init; }
    [JsonRequired] public PreviewExecutionPhase Phase { get; init; }
    [JsonRequired] public PreviewInputSnapshot? Inputs { get; init; }
    [JsonRequired] public PreviewRunReceipt? Run { get; init; }
    [JsonRequired] public PreviewCheckpointIntent? PendingRun { get; init; }
    [JsonRequired] public IReadOnlyList<PreviewModelDispatch> Dispatches { get; init; } = [];
    [JsonRequired] public string? Failure { get; init; }
    [JsonRequired] public PreviewInputSnapshot? FinalInputs { get; init; }
    [JsonRequired] public PreviewReadinessProof? Proof { get; init; }
    internal string CalculateChecksum() => FactoryModelsValidation.Hash(this with { Checksum = "" });
    internal PreviewEvaluationDocument Initial() => new(Protocol, Id, Product, FactoryId, OperationId, CreatedAt);
}

internal sealed partial class PreviewExecutionStore(PreviewCapabilitySnapshot authority, TokenCredential credential, HttpClient http, TimeProvider clock)
    : IRunStore, IPreviewModelDispatchJournal
{
    private readonly AzureCosmosDocumentGateway gateway = new(
        new FactoryWorkloadCredential(credential, authority.Manifest.Identity.TenantId, authority.Council, clock), http);
    private readonly string product = authority.Manifest.Identity.ProductKey;
    private readonly string endpoint = "https://" + FactoryPreviewValidation.Prefix(authority.Manifest.Identity.FactoryId) + "-cosmos.documents.azure.com";
    internal string Id { get; } = FactoryPreviewExecution.Id(authority.Manifest.Identity.FactoryId, authority.Capability.OperationId);
    private string? version;
    private bool poisoned;
    private Guid owner;
    private PreviewEvaluationDocument? document;
    private CosmosRunStore? runs;
    internal PreviewEvaluationDocument Document => document ?? throw new InvalidOperationException("Preview state must be read first.");
    internal string RunId => Id + "-run";

    internal async Task<PreviewExecutionSeed> PrepareAsync(TimeProvider clock, CancellationToken token, bool readOnly = false)
    {
        var native = await gateway.ReadCheckpointAsync(endpoint, product, FactoryPreviewValidation.PreviewContainer, product, Id, token);
        if (native is null)
        {
            if (readOnly) throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ReferencedPreviewSeedMissingNoReinitialization");
            var initial = new PreviewEvaluationDocument(1, Id, product, authority.Manifest.Identity.FactoryId, authority.Capability.OperationId, clock.GetUtcNow());
            if (authority.SeedIntent is { } intent)
            {
                if (intent.Endpoint != endpoint || intent.Database != product || intent.Container != FactoryPreviewValidation.PreviewContainer
                    || intent.Partition != product || intent.Policy != FactoryPreviewExecution.Policy) throw new JsonException();
                using var seedBody = JsonDocument.Parse(intent.Document);
                var reviewed = seedBody.RootElement.Deserialize<PreviewEvaluationDocument>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
                if (reviewed.Checksum != reviewed.CalculateChecksum())
                    throw new PreviewCapabilityException(FactoryCharterState.Unknown, "NativeSeedIntentChecksumChanged");
                if (reviewed.CreatedAt > clock.GetUtcNow())
                    throw new PreviewCapabilityException(FactoryCharterState.Unknown, "NativeSeedIntentClockUnconfirmed");
                initial = Parse(seedBody.RootElement);
            }
            initial = initial with { Checksum = initial.CalculateChecksum() };
            native = await gateway.WriteCheckpointAsync(endpoint, product, FactoryPreviewValidation.PreviewContainer, product, Id,
                authority.SeedIntent?.Document ?? JsonSerializer.Serialize(initial, FactoryGitHubJson.Options), null, token);
        }
        using var body = JsonDocument.Parse(native.Json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var document = Parse(body.RootElement);
        if (document.Phase != PreviewExecutionPhase.Prepared || document.Owner != Guid.Empty || document.Checksum != document.Initial().CalculateChecksum())
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewStateAlreadyInUse");
        var seed = new PreviewExecutionSeed(Id, Resource(body.RootElement), native.ETag, document.Checksum);
        if (authority.SeedIntent is { } intended)
        {
            using var expected = JsonDocument.Parse(intended.Document);
            if (seed.Checksum != expected.RootElement.GetProperty("checksum").GetString())
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ExistingNativeSeedDoesNotMatchReviewedIntent");
        }
        if (authority.Capability.Configuration.Execution is { } configuration && configuration.Seed != seed)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewSeedReceiptChanged");
        return seed;
    }

    internal async Task<ConveyorRun?> ReadAsync(CancellationToken token)
    {
        var seed = authority.Capability.Configuration.Execution?.Seed
            ?? throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewExecutionSeedRequired");
        var native = await gateway.ReadCheckpointAsync(endpoint, product, FactoryPreviewValidation.PreviewContainer, product, Id, token)
            ?? throw new PreviewCapabilityException(FactoryCharterState.Unknown, "SeededPreviewStateMissingNoReinitialization");
        using var body = JsonDocument.Parse(native.Json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        document = Parse(body.RootElement);
        if (seed.Id != Id || seed.ResourceId != Resource(body.RootElement) || seed.Checksum != Document.Initial().CalculateChecksum()
            || Document.Phase == PreviewExecutionPhase.Prepared && native.ETag != seed.ETag)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewSeedReceiptChanged");
        version = native.ETag;
        runs = new(endpoint, product, "runs", product, gateway, ConveyorProfile.OnboardingPreview);
        var run = await runs.LoadAsync(RunId, token);
        if (run is null)
        {
            if (Document.Run is not null || Document.PendingRun is not null)
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ReferencedPreviewRunMissing");
            return null;
        }
        var receipt = await RunReceiptAsync(run, token);
        if (Document.PendingRun is { } pending)
        {
            if (pending.Checksum != receipt.Checksum || Document.Run is { } previous && previous.ResourceId != receipt.ResourceId)
                throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewCheckpointDispatchUnconfirmed");
        }
        else if (Document.Run != receipt)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewRunReceiptChanged");
        return run;
    }

    internal async Task ClaimAsync(ConveyorRun? run, CancellationToken token)
    {
        if (Document.Dispatches.Any(d => d.State is PreviewModelDispatchState.Dispatched or PreviewModelDispatchState.Unknown))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PriorPreviewModelDispatchUnconfirmedNoReplay");
        owner = Guid.NewGuid();
        await WriteAsync(Document with { Owner = owner, Phase = PreviewExecutionPhase.Executing, PendingRun = null,
            Run = run is null ? null : await RunReceiptAsync(run, token) }, token);
    }

    internal Task CaptureAsync(PreviewInputSnapshot inputs, CancellationToken token) =>
        WriteAsync(Document with { Inputs = inputs }, token);

    internal Task FinishAsync(ConveyorRun run, CancellationToken token) =>
        WriteAsync(Document with { Phase = run.Status == RunStatus.Error ? PreviewExecutionPhase.Failed : PreviewExecutionPhase.Evaluated,
            Failure = run.FailureReason }, token);

    internal Task ProveAsync(PreviewInputSnapshot current, IReadOnlyList<PreviewCandidateProof> candidates, CancellationToken token)
    {
        var run = Document.Run ?? throw new JsonException();
        var inputs = Document.Inputs ?? throw new JsonException();
        var proof = new PreviewReadinessProof(1, Document.FactoryId, Document.OperationId, Id, authority.Revision,
            inputs.Revision, current.Revision, FactoryModelsValidation.Hash(inputs.Context), FactoryModelsValidation.Hash(inputs.Context.Judgment),
            run.Checksum, run.ResourceId, run.ETag, FactoryModelsValidation.Hash(Document.Dispatches), clock.GetUtcNow(), candidates);
        return WriteAsync(Document with { Phase = PreviewExecutionPhase.Evaluated, FinalInputs = current, Proof = proof }, token);
    }

    public Task<ConveyorRun?> LoadAsync(string runId, CancellationToken token) =>
        runId == RunId && runs is not null ? runs.LoadAsync(runId, token) : throw new InvalidOperationException("Exact initialized preview run required.");

    public async Task SaveAsync(ConveyorRun run, string station, CancellationToken token)
    {
        if (run.Id != RunId || run.Profile != ConveyorProfile.OnboardingPreview || !run.DryRun
            || Document.Inputs is null || FactoryModelsValidation.Hash(run.EvaluationContext) != FactoryModelsValidation.Hash(Document.Inputs.Context)
            || run.FiledIssues.Count != 0)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ExactNonpublishingPreviewRunRequired");
        var checksum = FactoryModelsValidation.Hash(run);
        await WriteAsync(Document with { PendingRun = new(checksum, station) }, token);
        try
        {
            await runs!.SaveAsync(run, station, token);
            var receipt = await RunReceiptAsync(run, token);
            await WriteAsync(Document with { Run = receipt, PendingRun = null }, token);
        }
        catch { poisoned = true; throw; }
    }

    private async Task<PreviewRunReceipt> RunReceiptAsync(ConveyorRun run, CancellationToken token)
    {
        var native = await gateway.ReadCheckpointAsync(endpoint, product, "runs", product, RunId, token) ?? throw new JsonException();
        using var body = JsonDocument.Parse(native.Json);
        var checksum = body.RootElement.GetProperty("onboardingChecksum").GetString();
        if (checksum != FactoryModelsValidation.Hash(run) || native.ETag != runs!.CheckpointETag(RunId))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "ExactNativePreviewRunRequired");
        return new(RunId, Resource(body.RootElement), native.ETag, checksum);
    }

    private async Task WriteAsync(PreviewEvaluationDocument next, CancellationToken token)
    {
        if (poisoned || version is null || owner == Guid.Empty || next.Owner != owner)
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewWriterUnconfirmedNoRetry");
        next = next with { Checksum = next.CalculateChecksum() };
        try
        {
            var saved = await gateway.WriteCheckpointAsync(endpoint, product, FactoryPreviewValidation.PreviewContainer, product, Id,
                JsonSerializer.Serialize(next, FactoryGitHubJson.Options), version, token);
            version = saved.ETag;
            document = next;
        }
        catch { poisoned = true; throw; }
    }

    private PreviewEvaluationDocument Parse(JsonElement body)
    {
        var payload = JsonNode.Parse(body.GetRawText())!.AsObject();
        foreach (var property in AzureCosmosDocumentGateway.SystemProperties) payload.Remove(property);
        var document = payload.Deserialize<PreviewEvaluationDocument>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
        Validate(document);
        return document;
    }

    private static string Resource(JsonElement body)
    {
        var resource = body.GetProperty("_rid").GetString() ?? throw new JsonException();
        if (resource.Length > 512 || !Convert.TryFromBase64String(resource, stackalloc byte[384], out var bytes) || bytes == 0)
            throw new JsonException("Invalid native preview resource identity.");
        return resource;
    }

    private void Validate(PreviewEvaluationDocument document)
    {
        if (document.Protocol != 1 || document.Kind != "council-preview-evaluation" || document.Profile != ConveyorProfile.OnboardingPreview
            || document.Id != Id || document.Product != product
            || document.FactoryId != authority.Manifest.Identity.FactoryId || document.OperationId != authority.Capability.OperationId
            || document.CreatedAt == default || document.CreatedAt.Offset != TimeSpan.Zero || document.CreatedAt > clock.GetUtcNow()
            || !Enum.IsDefined(document.Phase) || document.Checksum != document.CalculateChecksum()
            || document.Phase == PreviewExecutionPhase.Prepared && document.Checksum != document.Initial().CalculateChecksum()
            || document.Phase != PreviewExecutionPhase.Prepared && document.Owner == Guid.Empty
            || document.Inputs is null && (document.Run is not null || document.PendingRun is not null || document.Dispatches.Count != 0)
            || document.Phase is PreviewExecutionPhase.Evaluated or PreviewExecutionPhase.Failed && (document.Run is null || document.PendingRun is not null)
            || (document.FinalInputs is null) != (document.Proof is null)
            || document.Dispatches.Any(d => d is null) || document.Dispatches.Count > FactoryPreviewExecution.Policy.MaximumDispatches
            || document.Dispatches.Select(d => d.Identity).Distinct(StringComparer.Ordinal).Count() != document.Dispatches.Count
            || document.Inputs is { } inputs && (inputs.Revision != inputs.CalculateRevision() || inputs.Request.OperationId != document.OperationId
                || inputs.CapabilityRevision != authority.Revision || inputs.Context.FactoryId != document.FactoryId
                || FactoryModelsValidation.Hash(inputs.ExecutionConfiguration) != FactoryModelsValidation.Hash(authority.Capability.Configuration.Execution)))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewExecutionStateUnconfirmed");
        foreach (var dispatch in document.Dispatches) ValidateDispatch(dispatch);
        if (document.FinalInputs is { } final && final.Revision != final.CalculateRevision())
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewFinalInputsUnconfirmed");
        if (document.Proof is { } proof && (document.Phase != PreviewExecutionPhase.Evaluated || document.PendingRun is not null
            || document.Inputs is null || document.FinalInputs is null || document.Run is null || proof.Protocol != 1
            || proof.FactoryId != document.FactoryId || proof.OperationId != document.OperationId || proof.PreviewId != Id
            || proof.CapabilityRevision != authority.Revision || proof.InputRevision != document.Inputs.Revision
            || proof.FinalInputRevision != document.FinalInputs.Revision || proof.RunChecksum != document.Run.Checksum
            || proof.RunResourceId != document.Run.ResourceId || proof.RunETag != document.Run.ETag
            || proof.EvaluationRevision != FactoryModelsValidation.Hash(document.Inputs.Context)
            || proof.JudgmentRevision != FactoryModelsValidation.Hash(document.Inputs.Context.Judgment)
            || proof.DispatchRevision != FactoryModelsValidation.Hash(document.Dispatches) || proof.CompletedAt > clock.GetUtcNow()
            || proof.CompletedAt < document.Inputs.CapturedAt))
            throw new PreviewCapabilityException(FactoryCharterState.Unknown, "PreviewProofBindingsUnconfirmed");
    }
}

public static partial class RuntimeVerbs
{
    public static Task<PreviewExecutionSeed> InspectPreviewPreparationAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        PreviewCapabilityRequest request, CancellationToken token) =>
        RuntimeWorkflow.PreparePreviewExecutionAsync(settings, dependencies, request, dependencies.PreviewHttp ?? PreviewHttp, token, readOnly: true);
    public static Task<PreviewExecutionSeed> PreparePreviewExecutionAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        PreviewCapabilityRequest request, CancellationToken token) =>
        RuntimeWorkflow.PreparePreviewExecutionAsync(settings, dependencies, request, dependencies.PreviewHttp ?? PreviewHttp, token);
}

internal static partial class RuntimeWorkflow
{
    internal static async Task<PreviewExecutionSeed> PreparePreviewExecutionAsync(FactoryHostSettings settings, RuntimeDependencies dependencies,
        PreviewCapabilityRequest request, HttpClient http, CancellationToken cancellationToken, bool readOnly = false)
    {
        using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, dependencies.PreviewClock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = pending.Token;
        var credential = dependencies.PreviewCredential ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
        var authority = await new AzureFactoryPreviewCapability(settings, PreviewHostKind.Council, credential, http, dependencies.PreviewClock).CheckAsync(request, token);
        if (!readOnly && (authority.Capability.Configuration.Execution is not null
            || authority.Publication is { Phase: not PreviewPublicationPhase.SeedDispatched }))
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewSeedCannotBeReinitialized");
        var seed = await new PreviewExecutionStore(authority, credential, http, dependencies.PreviewClock).PrepareAsync(dependencies.PreviewClock, token, readOnly);
        var current = await new AzureFactoryPreviewCapability(settings, PreviewHostKind.Council, credential, http, dependencies.PreviewClock).CheckAsync(request, token);
        if (current.Revision != authority.Revision)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "PreviewCapabilityChangedDuringPreparation");
        return seed;
    }
}
