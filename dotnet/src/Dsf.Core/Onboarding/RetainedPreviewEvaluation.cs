using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsf.Core.Onboarding;

public enum PreviewExecutionPhase { Prepared, Executing, Evaluated, Failed }
public enum PreviewWorkState { Queued, Running, Released, Unknown }
public sealed record PreviewRunReceipt(string Id, string ResourceId, string ETag, string Checksum)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeDocumentSha256 { get; init; }
}
public sealed record PreviewCheckpointIntent(string Checksum, string Station);
public sealed record PreviewWorkAdmission(Guid Id, Guid WorkerId, PreviewCapabilityRequest Request,
    string CapabilityRevision, DateTimeOffset AdmittedAt, PreviewWorkState State, PreviewReadiness Readiness, string? Reason);
public sealed record PreviewEvaluationDocument(
    int Protocol, string Id, string Product, Guid FactoryId, Guid OperationId, DateTimeOffset CreatedAt)
{
    [JsonRequired] public string Kind { get; init; } = "council-preview-evaluation";
    [JsonRequired] public string Profile { get; init; } = "OnboardingPreview";
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreviewWorkAdmission? Admission { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FactoryLiveExecutionScope? LiveScope { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FactoryPublicationDecisionScope? DecisionScope { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SourceRefreshDispatch>? SourceRefresh { get; init; }
    public string CalculateChecksum() => FactoryModelsValidation.Hash(this with { Checksum = "" });
    public PreviewEvaluationDocument Initial() => new(Protocol, Id, Product, FactoryId, OperationId, CreatedAt)
        { LiveScope = LiveScope, DecisionScope = DecisionScope, Kind = Kind };
    public static PreviewEvaluationDocument ReadNative(JsonElement body)
    {
        var payload = JsonNode.Parse(body.GetRawText())!.AsObject();
        foreach (var property in new[] { "_rid", "_self", "_etag", "_attachments", "_ts" }) payload.Remove(property);
        return payload.Deserialize<PreviewEvaluationDocument>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
    }
}
