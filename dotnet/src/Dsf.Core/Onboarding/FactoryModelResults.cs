using System.Text.Json;

namespace Dsf.Core.Onboarding;

public static partial class FactoryModelsValidation
{
    public static void ValidateTextResult(FactoryModels authority, string route, ModelTextResult result)
    {
        var observation = result.Observation;
        ValidateObservation(authority, route, observation);
        if (result.Text.Length is < 40 or > 8192 || observation.OutputSha256 != Hash(result.Text)
            || observation.OutputCharacters != result.Text.Length || observation.Groups.Count != 0 || observation.Dimensions != 0)
            throw InvalidResult();
        if (route != "synthesis")
        {
            var text = result.Text.Trim();
            var prefix = text.StartsWith("NO-GO:", StringComparison.OrdinalIgnoreCase) ? 6
                : text.StartsWith("GO:", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
            if (prefix == 0 || text[prefix..].Trim().Length < 40
                || text[prefix..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 8) throw InvalidResult();
        }
    }

    public static void ValidateDeduplicationResult(FactoryModels authority, IReadOnlyList<ModelText> input, ModelDeduplicationResult result)
    {
        ValidateObservation(authority, "embedding", result.Observation);
        if (result.Groups.Count is < 1 or > 32 || result.Groups.Any(g => g is null || g.Count is < 1 or > 32)
            || !result.Groups.SelectMany(g => g).Order(StringComparer.Ordinal).SequenceEqual(input.Select(i => i.Id).Order(StringComparer.Ordinal))
            || Hash(result.Groups) != Hash(result.Observation.Groups))
            throw InvalidResult();
        var output = JsonSerializer.Serialize(result.Groups);
        if (result.Observation.OutputSha256 != Hash(output) || result.Observation.OutputCharacters != output.Length)
            throw InvalidResult();
    }

    private static void ValidateObservation(FactoryModels authority, string name, ModelWorkloadObservation observation)
    {
        var route = authority.Routes!.Single(r => r.Selection.Name == name).Selection;
        if (observation.Route != name || observation.ConfigurationRevision != authority.ApprovalFingerprint
            || observation.State != ModelCapabilityState.Verified || observation.Reason != ModelCapabilityReason.None
            || observation.ResponseModel != route.Model && observation.ResponseModel != route.Model + "-" + route.Version
            || observation.RequestId is not null && (!Guid.TryParse(observation.RequestId, out var id) || id == Guid.Empty)
            || observation.Dimensions != route.Dimensions)
            throw InvalidResult();
    }

    private static ModelCapabilityException InvalidResult() =>
        Failure(ModelCapabilityReason.MalformedResponse, "result", "Exact complete native model output and observation required.");
}
