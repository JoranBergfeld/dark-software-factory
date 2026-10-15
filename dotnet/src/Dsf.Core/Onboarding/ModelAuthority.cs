namespace Dsf.Core.Onboarding;

public sealed record ModelAuthorityRequest(ModelCapabilityReference Capability, Guid CheckId);
public sealed record ModelRouteAuthority(string Route, ModelResourceObservation Account,
    ModelResourceObservation Deployment, string RoleRevision, ModelResourceObservation Grant);
/// <summary>Request-bound native control-plane observations, not renewed inference proof or governing authority.</summary>
public sealed record ModelAuthorityResult(Guid CheckId, ModelCapabilityReference Capability, string PrincipalId,
    IReadOnlyList<ModelRouteAuthority> Routes)
{
    [System.Text.Json.Serialization.JsonRequired]
    public int Protocol { get; init; } = 1;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ContinuationProtocol { get; init; }
}

public static partial class FactoryModelsValidation
{
    public static void ValidateNativeAuthority(FactoryModels authority, Guid checkId, ModelAuthorityResult result)
    {
        var reference = new ModelCapabilityReference(authority.OperationId, authority.ApprovalFingerprint,
            authority.LastProofReference!, authority.LastProofSha256!);
        if (checkId == Guid.Empty || result.Protocol != 1 || result.CheckId != checkId || result.Capability != reference
            || result.PrincipalId != authority.Bindings![0].PrincipalId || result.Routes is null
            || result.Routes.Count != RouteNames.Count)
            throw Failure(ModelCapabilityReason.AuthorityChanged, "authority", "Fresh exact native authority response required; no cached capability substitute.");
        for (var i = 0; i < RouteNames.Count; i++)
        {
            var route = authority.Routes![i];
            var grant = authority.Grants!.Single(g => g.Scope == route.Selection.AccountResourceId);
            var deployment = route.Selection.Create
                ? Receipt(DeploymentTarget(route.Selection)) : route.DeploymentObservation;
            var observed = result.Routes[i];
            if (observed is null || observed.Route != route.Selection.Name || observed.Account != route.AccountObservation
                || observed.Deployment != deployment || observed.RoleRevision != grant.RoleRevision || observed.Grant != Receipt(grant.Target))
                throw Failure(ModelCapabilityReason.AuthorityChanged, route.Selection.Name, "Current native model authority differs from exact approved provenance.");
        }

        ModelResourceObservation Receipt(string target)
        {
            var effect = authority.Effects!.Single(e => e.Target == target);
            return new(target, effect.NativeRevision!, effect.Incarnation!);
        }
    }
}
