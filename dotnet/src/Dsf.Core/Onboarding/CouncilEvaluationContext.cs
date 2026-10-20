namespace Dsf.Core.Onboarding;

/// <summary>
/// Inputs to supervised council reasoning, not admission authority or readiness proof.
/// The runtime must verify current adoption, complete analysis, sources and native model routes before supplying them.
/// </summary>
public sealed record CouncilEvaluationContext(
    Guid FactoryId,
    string ManifestRevision,
    CharterAdoption Adoption,
    CharterEvidence Intent,
    CouncilApplicationScope Application,
    IReadOnlyList<CouncilEvidenceProvenance> Evidence,
    CouncilEvaluationModels Models,
    CouncilJudgmentConfiguration Judgment);

public sealed record CouncilApplicationScope(
    string Environment,
    IReadOnlyList<string> ApplicationResourceIds,
    IReadOnlyList<string> EvidenceBackendIds);

/// <summary>
/// Source observation binding. ContentSha256 uses FactoryModelsValidation.Hash on
/// the original evidence summary; the reference identifies privately persisted source proof.
/// </summary>
public sealed record CouncilEvidenceProvenance(
    string SourceKind,
    string Reference,
    string ContentSha256,
    string ConfigurationRevision,
    string ObservationReference,
    DateTimeOffset ObservedAt);

public sealed record CouncilEvaluationModels(string Revision, IReadOnlyList<ModelRouteSelection> Routes);
public sealed record CouncilLensConfiguration(string Name, double Weight);
public sealed record CouncilJudgmentConfiguration(
    int Rounds, double Threshold, IReadOnlyList<CouncilLensConfiguration> Lenses);
