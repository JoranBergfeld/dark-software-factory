namespace Dsf.FeatureCouncil.Conveyor;

/// <summary>
/// A deliberation lens's typed judgment on a proposal: go, no-go, or a deliberate
/// abstention when the lens has nothing decisive to say. Never a raw score -- the
/// weighted synthesizer, not the lens, is where a numeric confidence comes from.
/// </summary>
public enum LensPosition
{
    Go,
    NoGo,
    Abstain,
}

/// <summary>
/// One lens's stated position on a proposal, with the rationale it gave and the
/// weight the synthesizer combines it with. A pure data record -- the
/// synthesizer's <see cref="LensSynthesizer.Synthesize"/> only ever needs a list
/// of these, never the lens instance that produced them, which is what lets
/// station tests fix a set of verdicts directly instead of driving real lenses.
/// </summary>
public sealed record LensVerdict(string LensName, LensPosition Position, string Rationale, double Weight);

/// <summary>
/// The station-level outcome the weighted synthesizer reaches over one
/// proposal's final lens verdicts: whether to proceed, the weighted confidence
/// behind that call, and whether the lenses disagreed (at least one <see
/// cref="LensPosition.Go"/> and at least one <see cref="LensPosition.NoGo"/>
/// among them) -- recorded so a station's audit trail never hides a split
/// council behind a single number.
/// </summary>
public sealed record LensSynthesis(bool Proceed, double Confidence, bool Disagreement, IReadOnlyList<LensVerdict> Verdicts);

/// <summary>
/// One configurable deliberation angle S5 council weighs a proposal through --
/// value, cost, feasibility, security, strategic fit, or any other angle a
/// composition wires in. Runs across one or two rounds per proposal: an initial
/// round where <paramref name="priorRoundVerdicts"/> is empty, then a
/// see-and-revise round where it carries every lens's initial verdict, so a lens
/// can reconsider its position having seen where the others landed. The real
/// implementation (<see cref="ModelDeliberationLens"/>) reasons over the
/// proposal through the model client; tests substitute a fixed-position double
/// so a station test can assert unanimity, disagreement, or a threshold-crossing
/// confidence without a live model call.
/// </summary>
public interface IDeliberationLens
{
    string Name { get; }

    double Weight { get; }

    Task<LensVerdict> DeliberateAsync(
        Proposal proposal,
        ConveyorRun run,
        IReadOnlyList<LensVerdict> priorRoundVerdicts,
        IModelClient modelClient,
        CancellationToken cancellationToken);
}

/// <summary>
/// Combines a proposal's final lens verdicts into one station-level
/// recommendation: a deterministic, pure function of the verdicts and the
/// governed threshold, so the exact same set of positions always yields the
/// exact same proceed/reject call and confidence, independent of anything a
/// lens's own model call did to arrive at those positions. Confidence is the
/// weight-normalized average of each verdict's position score (go = 1, abstain
/// = 0.5 -- a genuine "no opinion", not a vote against -- no-go = 0); the
/// station proceeds when that confidence clears <paramref name="threshold"/>,
/// exactly the same governed bar the prior evidence-ratio calculation compared
/// against, so a Control Center threshold write still changes verdicts the same
/// way it always has.
/// </summary>
public static class LensSynthesizer
{
    public static LensSynthesis Synthesize(IReadOnlyList<LensVerdict> verdicts, double threshold)
    {
        if (verdicts.Count == 0)
        {
            return new LensSynthesis(Proceed: false, Confidence: 0d, Disagreement: false, Verdicts: verdicts);
        }

        var totalWeight = verdicts.Sum(verdict => verdict.Weight);
        var confidence = totalWeight <= 0
            ? 0d
            : verdicts.Sum(verdict => verdict.Weight * PositionScore(verdict.Position)) / totalWeight;

        var disagreement =
            verdicts.Any(verdict => verdict.Position == LensPosition.Go)
            && verdicts.Any(verdict => verdict.Position == LensPosition.NoGo);

        return new LensSynthesis(Proceed: confidence >= threshold, Confidence: confidence, disagreement, verdicts);
    }

    private static double PositionScore(LensPosition position) => position switch
    {
        LensPosition.Go => 1d,
        LensPosition.Abstain => 0.5d,
        LensPosition.NoGo => 0d,
        _ => 0d,
    };
}

/// <summary>
/// A deliberation lens that reasons over a proposal through the model client:
/// asks it to weigh the proposal along this lens's configured focus and answer
/// with a leading <c>GO</c>, <c>NO-GO</c>, or <c>ABSTAIN</c> token followed by a
/// rationale, then deterministically parses that leading token into a <see
/// cref="LensPosition"/> -- the model supplies the judgment, but turning its
/// answer into a typed position is a fixed, reproducible parse, not another
/// model call. On the see-and-revise round, the prompt also states every other
/// lens's initial position, so the model can genuinely reconsider rather than
/// re-answering blind.
/// </summary>
public sealed class ModelDeliberationLens(string name, double weight, string focus) : IDeliberationLens
{
    public string Name { get; } = name;

    public double Weight { get; } = weight;

    /// <summary>Weighs customer and business value delivered.</summary>
    public static ModelDeliberationLens Value(double weight = 1d) =>
        new("value", weight, "the customer and business value this proposal would deliver if built");

    /// <summary>Weighs implementation and ongoing operational cost.</summary>
    public static ModelDeliberationLens Cost(double weight = 1d) =>
        new("cost", weight, "the implementation cost and ongoing operational cost of building this proposal");

    /// <summary>Weighs technical feasibility given the current architecture.</summary>
    public static ModelDeliberationLens Feasibility(double weight = 1d) =>
        new("feasibility", weight, "the technical feasibility of building this proposal given the current architecture");

    /// <summary>Weighs security and compliance risk introduced.</summary>
    public static ModelDeliberationLens Security(double weight = 1d) =>
        new("security", weight, "the security and compliance risk this proposal would introduce");

    /// <summary>Weighs fit with the product's strategic roadmap.</summary>
    public static ModelDeliberationLens StrategicFit(double weight = 1d) =>
        new("strategic-fit", weight, "how well this proposal fits the product's strategic roadmap");

    /// <summary>The five lenses S5 council weighs every proposal through when a composition wires no others in.</summary>
    public static IReadOnlyList<IDeliberationLens> Default() =>
        [Value(), Cost(), Feasibility(), Security(), StrategicFit()];

    public async Task<LensVerdict> DeliberateAsync(
        Proposal proposal,
        ConveyorRun run,
        IReadOnlyList<LensVerdict> priorRoundVerdicts,
        IModelClient modelClient,
        CancellationToken cancellationToken)
    {
        var answer = await modelClient.CompleteAsync(BuildPrompt(proposal, priorRoundVerdicts), cancellationToken);
        var (position, rationale) = ParseAnswer(answer);
        return new LensVerdict(Name, position, rationale, Weight);
    }

    private string BuildPrompt(Proposal proposal, IReadOnlyList<LensVerdict> priorRoundVerdicts)
    {
        var prompt = $"As the '{Name}' deliberation lens, weigh {focus} for proposal '{proposal.Title}' "
            + $"(evidence: {string.Join(", ", proposal.EvidenceReferences)}). "
            + "Answer with a leading GO, NO-GO, or ABSTAIN followed by a one-sentence rationale.";
        if (priorRoundVerdicts.Count == 0)
        {
            return prompt;
        }

        var others = string.Join(
            "; ", priorRoundVerdicts.Select(verdict => $"{verdict.LensName}={verdict.Position}"));
        return prompt + $" The other lenses' initial positions were: {others}. Reconsider yours if warranted.";
    }

    /// <summary>
    /// Parses the model's leading token into a typed position and keeps whatever
    /// follows as the rationale. An answer with no recognizable leading token is
    /// treated as an abstention -- a lens that did not answer decisively records
    /// no opinion, never a silent go or no-go.
    /// </summary>
    internal static (LensPosition Position, string Rationale) ParseAnswer(string? answer)
    {
        var trimmed = (answer ?? string.Empty).Trim();
        foreach (var (token, position) in LeadingTokens)
        {
            if (trimmed.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                var rest = trimmed[token.Length..].TrimStart(':', '-', '.', ',', ' ').Trim();
                return (position, rest.Length == 0 ? trimmed : rest);
            }
        }

        return (LensPosition.Abstain, trimmed);
    }

    // "NO-GO"/"NOGO" checked before "GO": StartsWith is a prefix match, so this
    // ordering is not strictly required for correctness, but keeps the more
    // specific tokens first for readability.
    private static readonly (string Token, LensPosition Position)[] LeadingTokens =
    [
        ("NO-GO", LensPosition.NoGo),
        ("NOGO", LensPosition.NoGo),
        ("GO", LensPosition.Go),
        ("ABSTAIN", LensPosition.Abstain),
    ];
}
