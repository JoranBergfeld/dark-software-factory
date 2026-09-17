using System.Text.Json.Serialization;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public sealed record PreviewInputPolicy(string Revision, int MaximumObservations, int ObservationCharacters, int SnapshotBytes, int RequestSeconds);
public sealed record PreviewInputConfiguration(PreviewInputPolicy Policy, CouncilJudgmentConfiguration Judgment);
public enum PreviewInputState { Captured }
public sealed record PreviewInputEvidence(string SourceKind, string Reference, string Summary);
public sealed record PreviewInputRecheck(string SourceRevision, string TelemetryAuthorityRevision,
    IReadOnlyDictionary<string, string> SupplementalAuthorityRevisions, string ModelAuthorityRevision);

/// <summary>Private captured inputs, not evaluation, durable readiness proof or activation authority.</summary>
public sealed record PreviewInputSnapshot(
    int Protocol, PreviewInputState State, PreviewCapabilityRequest Request, DateTimeOffset CapturedAt,
    FactoryManifest Manifest, FactoryPreviewCapability Capability, string CapabilityRevision, string GovernanceRevision,
    CouncilEvaluationContext Context, IReadOnlyList<PreviewInputEvidence> Observations,
    PreviewSourceContext Source, PreviewTelemetrySelection TelemetrySelection, FactoryTelemetryConfiguration TelemetryConfiguration,
    PreviewTelemetryEvidence Telemetry, PreviewSupplementalEvidence Supplemental, PreviewSupplementalConfiguration? SupplementalConfiguration,
    IReadOnlyList<PreviewModelObservation> ModelObservations, PreviewInputRecheck Recheck)
{
    [JsonRequired] public string Revision { get; init; } = "";
    public string CalculateRevision() => FactoryModelsValidation.Hash(this with { Revision = "" });
}

public static class FactoryPreviewInputValidation
{
    public static PreviewInputPolicy Policy { get; } = new("council-input-capture-v1", 32, 8192, 1024 * 1024, 1800);
    public static void Validate(PreviewInputConfiguration configuration)
    {
        var judgment = configuration.Judgment;
        if (configuration.Policy != Policy || judgment.Rounds is < 1 or > 2 || !double.IsFinite(judgment.Threshold)
            || judgment.Threshold is < 0 or > 1 || judgment.Lenses.Count is < 1 or > 5
            || judgment.Lenses.Any(l => l is null || !DeliberationSettings.LensNames.Contains(l.Name)
                || !double.IsFinite(l.Weight) || l.Weight <= 0)
            || judgment.Lenses.Select(l => l.Name).Distinct(StringComparer.Ordinal).Count() != judgment.Lenses.Count)
            throw new PreviewCapabilityException(FactoryCharterState.Blocked, "ApprovedPreviewInputPolicyAndJudgmentRequired");
    }
}
