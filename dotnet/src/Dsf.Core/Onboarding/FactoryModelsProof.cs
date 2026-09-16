using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public sealed record ModelCapabilityReference(Guid OperationId, string ConfigurationRevision, string ProofReference, string ProofSha256);
public sealed record ModelWorkloadRequest(ModelCapabilityReference Capability, string Input);
public sealed record ModelDeduplicationRequest(ModelCapabilityReference Capability, IReadOnlyList<ModelText> Items);

public static partial class FactoryModelsValidation
{
    public const int MaximumProbeSeconds = 300;
    public static TimeSpan ProofExecutionBudget(int validForSeconds) =>
        TimeSpan.FromSeconds(Math.Min(MaximumProbeSeconds, validForSeconds));
    public static TimeSpan ProofRequestBudget(int validForSeconds) =>
        ProofExecutionBudget(validForSeconds) + TimeSpan.FromSeconds(30);

    public static void ValidateProof(FactoryModels authority, ModelCapabilityProof proof, DateTimeOffset now, bool requireCurrent)
    {
        if (proof.FactoryId != authority.Configuration.FactoryId || proof.OperationId != authority.OperationId
            || proof.CheckId == Guid.Empty || proof.CheckId != authority.CheckId || proof.ConfigurationRevision != authority.ApprovalFingerprint
            || proof.PrincipalId != authority.Bindings![0].PrincipalId || proof.ObservedAt > now.AddMinutes(1)
            || proof.ExpiresAt - proof.ObservedAt != TimeSpan.FromSeconds(authority.Configuration.ValidForSeconds)
            || requireCurrent && proof.State == ModelCapabilityState.Verified && (proof.ExpiresAt <= now || proof.ObservedAt < now.AddMinutes(-5))
            || proof.Coverage != ModelCapabilityProof.CoverageStatement
            || proof.Observations.Count != 5 || proof.Observations.Any(o => o is null) || !proof.Observations.Select(o => o.Route).SequenceEqual(RouteNames)
            || proof.State != (proof.Observations.All(o => o.State == ModelCapabilityState.Verified) ? ModelCapabilityState.Verified : ModelCapabilityState.Blocked))
            throw new JsonException("Exact current complete model proof required.");
        for (var i = 0; i < proof.Observations.Count; i++)
        {
            var observation = proof.Observations[i];
            var route = authority.Routes![i].Selection;
            if (observation.ConfigurationRevision != authority.ApprovalFingerprint || observation.Groups.Count > 3
                || observation.RequestId is not null && (!Guid.TryParse(observation.RequestId, out var request)
                    || request == Guid.Empty || observation.RequestId != request.ToString())
                || observation.ResponseModel?.Length > 141 || observation.OutputSha256?.Length > 64
                || observation.ResponseModel is not null && observation.ResponseModel != route.Model && observation.ResponseModel != route.Model + "-" + route.Version
                || observation.OutputSha256 is not null && !observation.OutputSha256.All(char.IsAsciiHexDigit)
                || observation.OutputCharacters is < 0 or > 8192 || observation.Dimensions is < 0 or > 3072
                || observation.Groups.Any(g => g is null || g.Count > 3 || g.Any(id => id is not ("one" or "two" or "three"))))
                throw new JsonException();
            if (observation.State == ModelCapabilityState.Verified)
            {
                if (observation.Reason != ModelCapabilityReason.None
                    || observation.ResponseModel != route.Model && observation.ResponseModel != route.Model + "-" + route.Version
                    || observation.OutputSha256 is not { Length: 64 } || !observation.OutputSha256.All(char.IsAsciiHexDigit)
                    || observation.OutputCharacters <= 0 || observation.Dimensions != route.Dimensions
                    || i == 1 && (!observation.Groups.SelectMany(g => g).Order().SequenceEqual(new[] { "one", "three", "two" })
                        || observation.Groups.Count == 0)
                    || i != 1 && (observation.Groups.Count != 0 || observation.OutputCharacters < 40))
                    throw new JsonException();
            }
            else if (observation.State != ModelCapabilityState.Blocked || observation.Reason == ModelCapabilityReason.None)
                throw new JsonException();
        }
    }

    public static FactoryModels ReadVerified(FactoryManifest manifest, IReadOnlyDictionary<string, string> values,
        ModelCapabilityReference reference, DateTimeOffset now)
    {
        var authority = ReadPreparation(manifest, values);
        if (authority.State != ModelCapabilityState.Verified || authority.Stage != "observed"
            || authority.OperationId != reference.OperationId || authority.ApprovalFingerprint != reference.ConfigurationRevision
            || authority.LastProofReference != reference.ProofReference || authority.LastProofSha256 != reference.ProofSha256
            || authority.CheckId is null || reference.ProofReference != ProductConfigurationKeys.ModelProof(authority.OperationId, authority.CheckId.Value))
            throw new JsonException("Current verified model capability reference required.");
        var proof = JsonSerializer.Deserialize<ModelCapabilityProof>(values[reference.ProofReference], FactoryGitHubJson.Options) ?? throw new JsonException();
        if (Hash(proof) != reference.ProofSha256 || proof.State != ModelCapabilityState.Verified
            || authority.VerifiedUntil != proof.ExpiresAt || proof.ExpiresAt <= now) throw new JsonException();
        ValidateProof(authority, proof, now, requireCurrent: false);
        return authority with { Proof = proof };
    }
}
