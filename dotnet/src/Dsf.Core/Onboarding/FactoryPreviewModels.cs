namespace Dsf.Core.Onboarding;

public sealed record PreviewModelHostConfiguration(string Purpose, string RuntimeImage, string AudienceClientId)
{
    public const string PurposeName = "council-model-evaluation-v1";
}

public sealed record PreviewModelRequest(PreviewCapabilityRequest Preview, string Route, string? Input, IReadOnlyList<ModelText>? Items);
public sealed record PreviewModelResult(Guid RequestId, string InputSha256, string CapabilityRevision,
    ModelAuthorityResult Authority, ModelTextResult? Completion, ModelDeduplicationResult? Deduplication);
public sealed record PreviewModelObservation(Guid RequestId, string InputSha256, string CapabilityRevision,
    ModelAuthorityResult Authority, ModelWorkloadObservation? Output);
public sealed record PreviewModelAuthorityContext(Guid RequestId, string CapabilityRevision, ModelAuthorityResult Authority);

public interface IFactoryPreviewModelWorkloads : IFactoryModelWorkloads
{
    IReadOnlyList<PreviewModelObservation> Observations { get; }
    Task<ModelAuthorityResult> ReadAuthorityAsync(CancellationToken cancellationToken);
}
